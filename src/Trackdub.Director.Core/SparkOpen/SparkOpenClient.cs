using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Real Spark Open client over the Azure Foundry managed-compute deployment. Env-only config via
/// <see cref="SparkOpenAuth"/>; the key is never logged or stored.
///
/// Two paths, by design:
/// <list type="bullet">
/// <item><b>Judge</b> (<see cref="JudgeMeaningAsync"/>): a direct chat call that sends the meaning-v1
/// criteria, reads Spark Open's raw reasoning reply, and extracts the final JSON verdict STRICTLY —
/// a reply with no extractable JSON verdict is a failed judgement (<see cref="MeaningVerdict.Error"/>),
/// recorded with its usage and excluded from agreement, never collapsed into a Fail.</item>
/// <item><b>QA / translation</b> (<see cref="AsMetaClient"/>): a <see cref="MetaClient"/> whose
/// chat/completions requests flow through <see cref="SparkOpenChatHandler"/>, which rewrites the
/// reply to the final answer so the existing QA loop and translation service run unchanged with
/// Spark Open as the provider.</item>
/// </list>
/// </summary>
public sealed class SparkOpenClient : ISparkOpenClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _rewritingHttpClient;
    private readonly HttpClient _rawHttpClient;
    private readonly MetaClient _metaClient;
    private readonly SparkOpenConfig _config;
    private readonly bool _ownsHttpClients;

    /// <summary>Resolves the route from the environment and fails closed (zero calls) when it is incomplete.</summary>
    public SparkOpenClient()
        : this(SparkOpenAuth.Resolve(), new HttpClientHandler(), new HttpClientHandler(), ownsHttpClients: true)
    {
    }

    /// <summary>
    /// Builds a client over caller-supplied transports (stubs in tests, socket handlers in production).
    /// <paramref name="rewritingInner"/> backs the QA/translation path (wrapped by the Spark Open
    /// handler); <paramref name="rawInner"/> backs the judge path (raw replies, strict verdict parse).
    /// </summary>
    public SparkOpenClient(SparkOpenConfig config, HttpMessageHandler rewritingInner, HttpMessageHandler rawInner, bool ownsHttpClients = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(rewritingInner);
        ArgumentNullException.ThrowIfNull(rawInner);

        // The Bearer key is sent to this route on both paths; the same https-or-loopback rule the env
        // path enforces applies to an injected config too, so a plaintext non-loopback deployment URL
        // fails here before any request is built.
        SparkOpenAuth.RequireValidBaseUrl(_config.BaseUrl);

        _rewritingHttpClient = new HttpClient(new SparkOpenChatHandler(config, rewritingInner)) { Timeout = TimeSpan.FromSeconds(120) };
        _rawHttpClient = new HttpClient(rawInner) { Timeout = TimeSpan.FromSeconds(120) };
        _ownsHttpClients = ownsHttpClients;
        // Instance-scoped auth: the adapter never needs the hosted META_API_KEY. The handler re-signs
        // every request with the Spark Open key; the override only keeps MetaAuth from being read.
        _metaClient = new MetaClient(_rewritingHttpClient, _config.ApiKey);
    }

    public string Model => _config.Model;

    public string EndpointHost => _config.EndpointHost;

    public MetaClient AsMetaClient() => _metaClient;

    public async Task<MeaningCheckResult> JudgeMeaningAsync(
        JudgeMeaningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Candidate);

        try
        {
            string srcLang = MetaClient.LanguageDisplayName(request.SourceLanguage);
            string tgtLang = MetaClient.LanguageDisplayName(request.TargetLanguage);
            var payload = new
            {
                model = _config.Model,
                messages = new object[]
                {
                    // Same meaning-v1 criteria the hosted gate uses, plus the final-line JSON instruction
                    // so the verdict is separable from the reasoning Spark Open always produces.
                    new { role = "system", content = $"{MeaningGate.BuildSystemPrompt()}\n\n{SparkOpenChatHandler.FinalAnswerInstruction}" },
                    new { role = "user", content = MeaningGate.BuildUserPrompt(request.SourceText, request.Candidate, srcLang, tgtLang) },
                },
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using HttpResponseMessage response = await _rawHttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            string body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Azure error bodies carry a message field (deployment/request errors); keep a bounded
                // copy in the recorded reason so the report shows why the judgement failed, not just a status.
                return MeaningGate.ForError(BuildHttpErrorReason(response.StatusCode, new ReplyJson(body)), _config.Model, null, _config.EndpointHost);
            }

            return ParseJudgeReply(new ReplyJson(body));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return MeaningGate.ForError(ex.Message, _config.Model, null, _config.EndpointHost);
        }
    }

    /// <summary>
    /// Reads the raw chat reply, extracts the final JSON verdict strictly, and parses it with the gate
    /// normalizer. No extractable JSON verdict → Error (a failed judgement), never a guessed Pass/Fail.
    /// Usage is preserved from the reply even on a failed parse.
    /// </summary>
    private MeaningCheckResult ParseJudgeReply(ReplyJson body)
    {
        MetaClient.TokenUsage? usage = null;
        string rawContent;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body.Value);
            JsonElement root = doc.RootElement;
            usage = ReadUsage(root);
            rawContent = ReadAssistantContent(root);
        }
        catch (JsonException)
        {
            return MeaningGate.ForError("Spark Open reply was not valid JSON.", _config.Model, null, _config.EndpointHost);
        }

        if (!SparkOpenReasoningParser.TryExtractFinalJsonObject(rawContent, out string verdictJson, requireEndOfReply: true))
        {
            return MeaningGate.ForError("Spark Open reply carried no extractable final JSON verdict.", _config.Model, usage, _config.EndpointHost);
        }

        return ParseStrictMeaningVerdict(new ReplyJson(verdictJson), ProvenanceInfo, usage);
    }

    private static string ReadAssistantContent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!root.TryGetProperty("choices", out JsonElement choices))
        {
            return string.Empty;
        }

        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        if (choices[0].ValueKind != JsonValueKind.Object
            || !choices[0].TryGetProperty("message", out JsonElement message))
        {
            return string.Empty;
        }

        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;
    }

    private Provenance ProvenanceInfo => new(_config.Model, _config.EndpointHost);

    /// <summary>
    /// Builds the recorded reason for a failed judge call: the HTTP status plus a bounded copy of the
    /// error body's <c>message</c> (Azure deployments report deployment/request errors there), so the
    /// report shows why the call failed rather than only a status code.
    /// </summary>
    private static string BuildHttpErrorReason(HttpStatusCode status, ReplyJson body)
    {
        string? message = ExtractErrorMessage(body);
        return message is null
            ? $"Spark Open HTTP {(int)status}"
            : $"Spark Open HTTP {(int)status}: {message}";
    }

    private static string? ExtractErrorMessage(ReplyJson body)
    {
        if (string.IsNullOrWhiteSpace(body.Value))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body.Value);
            return ReadErrorMessage(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? message = ReadNestedErrorMessage(root) ?? ReadTopLevelMessage(root);
        return message is null ? null : Truncate(message);
    }

    private static string? ReadNestedErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error))
        {
            return null;
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return error.TryGetProperty("message", out JsonElement message) ? AsText(message) : null;
    }

    private static string? ReadTopLevelMessage(JsonElement root)
        => root.TryGetProperty("message", out JsonElement message) ? AsText(message) : null;

    private static string? AsText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = element.GetString()?.Trim() ?? string.Empty;
        return text.Length == 0 ? null : text;
    }

    private static string Truncate(string text)
    {
        const int maxLength = 200;
        return text.Length <= maxLength ? text : $"{text[..maxLength]}...";
    }

    /// <summary>
    /// Parses a meaning verdict STRICTLY: the verdict must be exactly "pass" or "fail" and any issues
    /// array must be a string array. <see cref="MeaningGate.ParseAndNormalize"/> turns a missing or
    /// unknown verdict, or a malformed issues array, into a Fail — and the judge runner would then
    /// score that Fail and can count agreement with a hosted Fail. A malformed Spark Open reply is a
    /// failed judgement instead: <see cref="MeaningVerdict.Error"/> with the billed usage preserved,
    /// never a guessed verdict. Mirrored by <see cref="FakeSparkOpenClient"/> so scripted offline
    /// judgements follow the same exclusion rule.
    /// </summary>
    internal static MeaningCheckResult ParseStrictMeaningVerdict(
        ReplyJson verdictJson,
        Provenance provenance,
        MetaClient.TokenUsage? usage)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(verdictJson.Value);
            return ParseVerdictRoot(doc.RootElement, verdictJson, provenance, usage);
        }
        catch (JsonException)
        {
            return Error("Spark Open final verdict was not valid JSON.", provenance, usage);
        }
    }

    private static MeaningCheckResult ParseVerdictRoot(JsonElement root, ReplyJson verdictJson, Provenance provenance, MetaClient.TokenUsage? usage)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Error("Spark Open final verdict was not a JSON object.", provenance, usage);
        }

        if (!TryReadVerdict(root, out string verdict))
        {
            return Error("Spark Open final verdict had no pass/fail verdict.", provenance, usage);
        }

        if (!IsPassOrFail(verdict))
        {
            return Error($"Spark Open final verdict was not pass/fail (got '{verdict}').", provenance, usage);
        }

        if (!IssuesAreStrings(root, out string? issueError))
        {
            return Error(issueError!, provenance, usage);
        }

        return MeaningGate.ParseAndNormalize(verdictJson.Value, provenance.Model, usage, provenance.EndpointHost);
    }

    private static bool TryReadVerdict(JsonElement root, out string verdict)
    {
        verdict = string.Empty;
        if (!root.TryGetProperty("verdict", out JsonElement verdictEl))
        {
            return false;
        }

        if (verdictEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        verdict = verdictEl.GetString()?.Trim() ?? string.Empty;
        return true;
    }

    private static bool IsPassOrFail(string verdict)
        => string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verdict, "fail", StringComparison.OrdinalIgnoreCase);

    private static bool IssuesAreStrings(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("issues", out JsonElement issuesEl))
        {
            return true;
        }

        if (issuesEl.ValueKind != JsonValueKind.Array)
        {
            error = "Spark Open final verdict 'issues' was not an array.";
            return false;
        }

        foreach (JsonElement item in issuesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "Spark Open final verdict 'issues' must be strings.";
                return false;
            }
        }

        return true;
    }

    private static MeaningCheckResult Error(string message, Provenance provenance, MetaClient.TokenUsage? usage)
        => MeaningGate.ForError(message, provenance.Model, usage, provenance.EndpointHost);

    private static MetaClient.TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new MetaClient.TokenUsage(
            GetInt(usage, new FieldName("prompt_tokens")),
            GetInt(usage, new FieldName("completion_tokens")),
            GetInt(usage, new FieldName("total_tokens")));
    }

    private static int GetInt(JsonElement obj, FieldName name)
        => obj.TryGetProperty(name.Value, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;

    public void Dispose()
    {
        // Ownership contract: when the transports are caller-supplied (ownsHttpClients: false), the
        // caller owns everything derived from them too — the MetaClient view included — so nothing is
        // disposed here. Only a self-owned client disposes its transports.
        if (_ownsHttpClients)
        {
            _metaClient.Dispose();
            _rewritingHttpClient.Dispose();
            _rawHttpClient.Dispose();
        }
    }

    internal readonly record struct ReplyJson(string Value);

    internal sealed record Provenance(string Model, string EndpointHost);

    private readonly record struct FieldName(string Value);
}
