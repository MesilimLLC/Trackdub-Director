using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// <see cref="IGlimmerClient"/> over an OpenAI-compatible chat-completions endpoint
/// (Azure Foundry managed compute for Muse-Glimmer-30B in Phase 2). HttpClient +
/// System.Text.Json only. The route comes from <see cref="GlimmerAuth"/> (env-only); the
/// key is set as a Bearer header for the single request and never logged, printed, or
/// stored. Phase 1 makes ZERO calls: no live run reaches this class, and it still fails
/// closed when the route is unset.
/// </summary>
public sealed class GlimmerClient : IGlimmerClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly CallBudget? _budget;

    public GlimmerClient(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }
    }

    private GlimmerClient(GlimmerClient parent, CallBudget budget)
    {
        _httpClient = parent._httpClient;
        _ownsClient = false;
        _budget = budget;
    }

    /// <summary>Returns a client that shares this connection and charges every request it sends to <paramref name="budget"/>.</summary>
    public GlimmerClient WithBudget(CallBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return new GlimmerClient(this, budget);
    }

    public async Task<GlimmerReply> ProposeAsync(GlimmerRecoveryTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // Fail closed: resolve the full route BEFORE any work, so a missing variable never
        // reaches the network. GlimmerAuth never echoes the key.
        GlimmerAuth.GlimmerRoute route = GlimmerAuth.GetRoute();

        var payload = new GlimmerChatRequest(
            route.Model,
            [
                new GlimmerChatMessage("system", GlimmerPrompt.BuildSystemPrompt()),
                new GlimmerChatMessage("user", GlimmerPrompt.BuildUserPrompt(turn)),
            ],
            route.ReasoningEffort);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.BaseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);

        string body = await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);
        return ParseReply(body, route.Model);
    }

    /// <summary>
    /// Exactly ONE Glimmer call that proves the route is live before the capped loop runs.
    /// Fails closed when the route is unset (zero calls), and throws <see cref="MetaApiException"/>
    /// on any failure so the caller stops before the loop. The reply MUST be the schema-constrained
    /// JSON object {"ok": true}; a non-200, an unparseable body, a missing/false "ok", or a returned
    /// model that is not a Glimmer model each fail the probe. Charges the shared budget like any send,
    /// so the probe counts toward the run's Glimmer call cap. Makes NO Meta call. The returned
    /// <see cref="GlimmerProbeResult"/> carries the endpoint host (never the key), the requested and
    /// returned model strings, usage, and wall seconds.
    /// </summary>
    public async Task<GlimmerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        GlimmerAuth.GlimmerRoute route = GlimmerAuth.GetRoute();
        string host = Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : route.BaseUrl;

        var payload = new GlimmerChatRequest(
            route.Model,
            [
                new GlimmerChatMessage("system", "You are a readiness probe. Reply with STRICT JSON only, exactly: {\"ok\": true}. No prose, no markdown fence."),
                new GlimmerChatMessage("user", "Return {\"ok\": true} if you can produce schema-constrained JSON."),
            ],
            route.ReasoningEffort);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.BaseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);

        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        string body = await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);
        double wallSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalSeconds;

        GlimmerChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GlimmerChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Glimmer probe returned an unparseable response: {Truncate(body)}", ex);
        }

        string returnedModel = parsed?.Model ?? string.Empty;
        string content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;

        if (!ProbeReplyIsOk(content))
        {
            throw new MetaApiException($"Glimmer probe reply was not the required {{\"ok\": true}}: {Truncate(content)}");
        }

        // The returned model must be a Glimmer model; a different served model means the deployment
        // is not the one under test, so stop before the loop.
        if (!IsGlimmerModel(returnedModel))
        {
            throw new MetaApiException($"Glimmer probe returned a non-Glimmer model '{returnedModel}'; expected a Glimmer model.");
        }

        return new GlimmerProbeResult(host, route.Model, returnedModel, ToUsage(parsed?.Usage), wallSeconds);
    }

    /// <summary>True when the JSON object has a boolean "ok" that is true. Tolerates surrounding prose.</summary>
    private static bool ProbeReplyIsOk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A returned/requested model counts as Glimmer when its id contains "glimmer" (case-insensitive).</summary>
    private static bool IsGlimmerModel(string? model)
        => !string.IsNullOrWhiteSpace(model) && model.Contains("glimmer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse the chat body into a single on-schema action. A reply that is not strict JSON,
    /// is not an object with a recognized action, or lacks the fields that action requires is
    /// flagged malformed — never repaired. The caller records a malformed reply as a failed
    /// attempt, so usage is preserved either way.
    /// </summary>
    private static GlimmerReply ParseReply(string body, string requestedModel)
    {
        GlimmerChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GlimmerChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Glimmer response: {Truncate(body)}", ex);
        }

        string model = parsed?.Model ?? requestedModel;
        MetaClient.TokenUsage? usage = ToUsage(parsed?.Usage);
        string content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
        {
            // An empty assistant message carries no action; treat as a malformed (failed) attempt.
            return GlimmerReply.ForMalformed(content, model, usage);
        }

        GlimmerAction? action = GlimmerActionParser.TryParse(content);
        return action is null
            ? GlimmerReply.ForMalformed(content, model, usage)
            : GlimmerReply.ForAction(action, content, model, usage);
    }

    private static MetaClient.TokenUsage? ToUsage(GlimmerUsageDto? usage)
        => usage is null ? null : new MetaClient.TokenUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);

    private async Task<string> SendForBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _budget?.Reserve();

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MetaApiException($"Glimmer request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetaApiException("Glimmer request timed out.", ex);
        }

        using (response)
        {
            string body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new MetaApiException($"Glimmer HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            return body;
        }
    }

    private static string Truncate(string body) => body.Length > 500 ? body[..500] : body;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    // Wire format — OpenAI-compatible chat completions.
    private sealed record GlimmerChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] GlimmerChatMessage[] Messages,
        [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null);

    private sealed record GlimmerChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GlimmerChatResponse(
        [property: JsonPropertyName("choices")] GlimmerChatChoice[]? Choices,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("usage")] GlimmerUsageDto? Usage);

    private sealed record GlimmerChatChoice(
        [property: JsonPropertyName("message")] GlimmerChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

    private sealed record GlimmerUsageDto(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
}
