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

        _rewritingHttpClient = new HttpClient(new SparkOpenChatHandler(config, rewritingInner)) { Timeout = TimeSpan.FromSeconds(120) };
        _rawHttpClient = new HttpClient(rawInner) { Timeout = TimeSpan.FromSeconds(120) };
        _ownsHttpClients = ownsHttpClients;
        _metaClient = new MetaClient(_rewritingHttpClient);
    }

    public string Model => _config.Model;

    public string EndpointHost => _config.EndpointHost;

    public MetaClient AsMetaClient() => _metaClient;

    public async Task<MeaningCheckResult> JudgeMeaningAsync(
        string sourceText,
        string candidate,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        try
        {
            string srcLang = MetaClient.LanguageDisplayName(sourceLanguage);
            string tgtLang = MetaClient.LanguageDisplayName(targetLanguage);
            var payload = new
            {
                model = _config.Model,
                messages = new object[]
                {
                    // Same meaning-v1 criteria the hosted gate uses, plus the final-line JSON instruction
                    // so the verdict is separable from the reasoning Spark Open always produces.
                    new { role = "system", content = $"{MeaningGate.BuildSystemPrompt()}\n\n{SparkOpenChatHandler.FinalAnswerInstruction}" },
                    new { role = "user", content = MeaningGate.BuildUserPrompt(sourceText, candidate, srcLang, tgtLang) },
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using HttpResponseMessage response = await _rawHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MeaningGate.ForError($"Spark Open HTTP {(int)response.StatusCode}", _config.Model, null, _config.EndpointHost);
            }

            return ParseJudgeReply(body);
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
    private MeaningCheckResult ParseJudgeReply(string body)
    {
        MetaClient.TokenUsage? usage = null;
        string rawContent;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            usage = ReadUsage(root);
            rawContent = root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return MeaningGate.ForError("Spark Open reply was not valid JSON.", _config.Model, null, _config.EndpointHost);
        }

        if (!SparkOpenReasoningParser.TryExtractFinalJsonObject(rawContent, out string verdictJson))
        {
            return MeaningGate.ForError("Spark Open reply carried no extractable final JSON verdict.", _config.Model, usage, _config.EndpointHost);
        }

        return MeaningGate.ParseAndNormalize(verdictJson, _config.Model, usage, _config.EndpointHost);
    }

    private static MetaClient.TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new MetaClient.TokenUsage(
            GetInt(usage, "prompt_tokens"),
            GetInt(usage, "completion_tokens"),
            GetInt(usage, "total_tokens"));
    }

    private static int GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    public void Dispose()
    {
        _metaClient.Dispose();
        if (_ownsHttpClients)
        {
            _rewritingHttpClient.Dispose();
            _rawHttpClient.Dispose();
        }
    }
}
