using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Makes the Azure Muse Spark Open deployment drop-in for the hosted chat/completions path,
/// so the existing QA revision loop (<see cref="QaService"/>) and the existing meaning gate
/// (<see cref="MetaClient.CheckMeaningAsync"/> → <see cref="MeaningGate"/>) run unchanged against
/// Spark Open. Nothing in <see cref="MetaClient"/>, <see cref="MetaAuth"/>, or <see cref="MeaningGate"/>
/// is modified.
///
/// On the request side it redirects any <c>/chat/completions</c> call to the Spark Open endpoint,
/// swaps the Authorization header to the Spark Open key, rewrites the <c>model</c> to the Spark Open
/// deployment, and appends a system instruction telling the model to end its reply with the answer
/// as a single JSON object on the final line (Spark Open always reasons, so the answer must be
/// separable from the reasoning that precedes it).
///
/// On the response side it extracts that final JSON object via <see cref="SparkOpenReasoningParser"/>
/// and rewrites the chat/completions body so the caller's <c>message.content</c> is exactly the
/// extracted answer, preserving the real Spark Open <c>usage</c> (reasoning tokens included). A reply
/// whose final answer cannot be extracted is surfaced as a FAILED call — the body is replaced with an
/// HTTP 502 the caller's error path records with usage — never guessed and never silently re-prompted.
///
/// The handler resolves <see cref="SparkOpenAuth"/> at construction, so a missing route fails closed
/// before any request is built, independent of the hosted credentials.
/// </summary>
public sealed class SparkOpenChatHandler : DelegatingHandler
{
    internal const string FinalAnswerInstruction =
        "You are running in a structured-output mode. You may reason first, but you MUST end your reply with the final answer as a single JSON object on the last line, and nothing after it. Output no prose after that final JSON object.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SparkOpenConfig _config;
    private int _requestCount;

    public SparkOpenChatHandler(SparkOpenConfig config, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>Count of chat/completions requests this handler actually forwarded to Spark Open. Lets tests assert zero calls on a fail-closed path.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsChatCompletion(request))
        {
            // Not a chat completion (e.g. a models listing): forward untouched to the Spark Open base.
            RedirectToSparkOpen(request, request.RequestUri?.AbsolutePath ?? "/");
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string originalBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string rewrittenBody = RewriteRequestBody(new JsonBody(originalBody));

        RedirectToSparkOpen(request, "/chat/completions");
        request.Content = new StringContent(rewrittenBody, Encoding.UTF8, "application/json");

        Interlocked.Increment(ref _requestCount);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return response;
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return RewriteResponse(response, new JsonBody(responseBody));
    }

    private static bool IsChatCompletion(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return false;
        }

        if (request.RequestUri is null)
        {
            return false;
        }

        return request.RequestUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal);
    }

    private void RedirectToSparkOpen(HttpRequestMessage request, string path)
    {
        request.RequestUri = new Uri($"{_config.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    }

    private string RewriteRequestBody(JsonBody body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body.Value);
        }
        catch (JsonException ex)
        {
            throw new SparkOpenException("Failed to rewrite the chat request for Spark Open.", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new SparkOpenException("Chat request body was not a JSON object.");
        }

        obj["model"] = _config.Model;

        // Spark Open always reasons and has no reasoning-off mode: drop reasoning_effort (hosted-only)
        // and inject the final-answer instruction so the answer is separable from the reasoning.
        obj.Remove("reasoning_effort");
        PrependFinalAnswerInstruction(obj);

        return obj.ToJsonString(JsonOptions);
    }

    private static void PrependFinalAnswerInstruction(JsonObject obj)
    {
        if (obj["messages"] is not JsonArray messages)
        {
            return;
        }

        // Fold the instruction into the existing system message when present, else prepend a new one,
        // so the model keeps exactly one system message and the lane's own prompt is preserved verbatim.
        foreach (JsonNode? node in messages)
        {
            if (node is JsonObject message
                && string.Equals(message["role"]?.GetValue<string>(), "system", StringComparison.Ordinal))
            {
                string existing = message["content"]?.GetValue<string>() ?? string.Empty;
                message["content"] = existing.Length == 0 ? FinalAnswerInstruction : $"{existing}\n\n{FinalAnswerInstruction}";
                return;
            }
        }

        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = FinalAnswerInstruction });
    }

    private HttpResponseMessage RewriteResponse(HttpResponseMessage response, JsonBody body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body.Value);
        }
        catch (JsonException)
        {
            return FailedCall(response, body, new FailureReason("Spark Open response was not valid JSON."));
        }

        if (!TryGetAssistantMessage(root, out JsonObject rootObject, out JsonObject message))
        {
            return FailedCall(response, body, new FailureReason("Spark Open response had no assistant message."));
        }

        string rawContent = message["content"]?.GetValue<string>() ?? string.Empty;
        if (!SparkOpenReasoningParser.TryExtractFinalAnswer(rawContent, out string finalAnswer))
        {
            // Preserve usage on the failed call: the API billed for it regardless.
            return FailedCall(response, body, new FailureReason("Spark Open reply carried no extractable final answer."));
        }

        // The injected instruction asks for a JSON final object, so a translation reply arrives as
        // {"translation":"..."} — unwrap it so timing estimates, meaning checks, and saved translations
        // see the plain line, never the wrapper. Meaning verdicts stay as JSON for the gate; a JSON
        // object with neither shape is a malformed translation reply and fails the call rather than
        // leaking raw JSON into the QA loop.
        if (!TryResolveAssistantContent(finalAnswer, out string content, out FailureReason? reason))
        {
            return FailedCall(response, body, reason!.Value);
        }

        message["content"] = content;
        rootObject["model"] ??= _config.Model;

        var rewritten = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rootObject.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
        };
        response.Dispose();
        return rewritten;
    }

    private static bool TryGetAssistantMessage(JsonNode? root, out JsonObject rootObject, out JsonObject message)
    {
        rootObject = null!;
        message = null!;
        if (root is not JsonObject obj)
        {
            return false;
        }

        if (obj["choices"] is not JsonArray { Count: > 0 } choices)
        {
            return false;
        }

        if (choices[0] is not JsonObject choice)
        {
            return false;
        }

        if (choice["message"] is not JsonObject assistant)
        {
            return false;
        }

        rootObject = obj;
        message = assistant;
        return true;
    }

    /// <summary>
    /// Resolves the extracted final answer to the content the caller should see. A plain-text answer
    /// (a translated line) passes through unchanged; a meaning verdict object stays as JSON for the
    /// gate; a translation-shaped <c>{"translation": "..."}</c> object is unwrapped to the plain
    /// string. A JSON object that is neither is a malformed translation reply and fails the call.
    /// </summary>
    private static bool TryResolveAssistantContent(string finalAnswer, out string content, out FailureReason? reason)
    {
        content = finalAnswer;
        reason = null;
        using JsonDocument? doc = TryParseJsonObject(finalAnswer);
        if (doc is null)
        {
            return true;
        }

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty("verdict", out _))
        {
            // A meaning verdict must never be unwrapped to a translation string: the gate needs the object.
            return true;
        }

        if (IsTranslationObject(root, out JsonElement translation))
        {
            content = translation.GetString()!.Trim();
            return true;
        }

        reason = new FailureReason("Spark Open final JSON object was neither a meaning verdict nor a {\"translation\":\"...\"} reply.");
        return false;
    }

    private static JsonDocument? TryParseJsonObject(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTranslationObject(JsonElement root, out JsonElement translation)
    {
        translation = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("translation", out JsonElement candidate))
        {
            return false;
        }

        if (candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.GetString()))
        {
            return false;
        }

        translation = candidate;
        return true;
    }

    /// <summary>
    /// Marks a reply as a failed call. The body preserves the original Spark Open <c>usage</c> so the
    /// caller's error path records tokens, and the 502 status drives the caller to its fail path rather
    /// than any retry under a different label.
    /// </summary>
    private static HttpResponseMessage FailedCall(HttpResponseMessage original, JsonBody body, FailureReason reason)
    {
        string usageJson = ExtractUsageJson(body);
        var payload = $"{{\"error\":{{\"message\":{JsonSerializer.Serialize(reason.Value)}}},\"usage\":{usageJson}}}";
        var failed = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        original.Dispose();
        return failed;
    }

    private static string ExtractUsageJson(JsonBody body)
    {
        try
        {
            if (JsonNode.Parse(body.Value) is JsonObject obj && obj["usage"] is JsonObject usage)
            {
                return usage.ToJsonString(JsonOptions);
            }
        }
        catch (JsonException)
        {
            // fall through to null usage
        }

        return "null";
    }

    private readonly record struct JsonBody(string Value);

    private readonly record struct FailureReason(string Value);
}
