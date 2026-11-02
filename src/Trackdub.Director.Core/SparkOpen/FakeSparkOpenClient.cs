namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// In-memory <see cref="ISparkOpenClient"/> for offline tests: no socket is ever opened, so Phase 1
/// makes zero live calls. Judge replies are scripted as raw Spark-Open-style reasoning strings and
/// parsed through the real <see cref="SparkOpenReasoningParser"/> and <see cref="MeaningGate"/>, so a
/// reasoning-prefixed reply is parsed to its final answer and an unextractable one fails closed —
/// exactly as the real client behaves. The QA-loop surface (<see cref="AsMetaClient"/>) is backed by a
/// caller-supplied <see cref="HttpMessageHandler"/> stub routed through the real
/// <see cref="SparkOpenChatHandler"/>.
/// </summary>
public sealed class FakeSparkOpenClient : ISparkOpenClient
{
    private readonly Queue<string> _judgeReplies;
    private readonly MetaClient? _metaClient;
    private readonly HttpClient? _httpClient;
    private int _judgeCalls;

    /// <summary>
    /// Every <see cref="JudgeMeaningAsync"/> call's source/candidate pair, in call order, so a test
    /// can assert the runner passed each item's actual text (not just the call count).
    /// </summary>
    public List<(string SourceText, string Candidate)> JudgeCallsRecorded { get; } = new();

    /// <param name="judgeReplies">
    /// Raw assistant replies for successive <see cref="JudgeMeaningAsync"/> calls, each a
    /// reasoning-prefixed string ending in a <c>{"verdict":...,"explanation":...}</c> line, or any
    /// string with no extractable final object to simulate a failed judgement.
    /// </param>
    /// <param name="qaInnerHandler">
    /// Optional transport stub for the QA-loop surface. When supplied, <see cref="AsMetaClient"/>
    /// returns a real <see cref="MetaClient"/> whose requests flow through the real Spark Open handler
    /// into this stub. When null, <see cref="AsMetaClient"/> throws.
    /// </param>
    public FakeSparkOpenClient(
        IEnumerable<string>? judgeReplies = null,
        HttpMessageHandler? qaInnerHandler = null,
        string model = "muse-spark-open-fake",
        string endpointHost = "spark-open.fake.local")
    {
        _judgeReplies = new Queue<string>(judgeReplies ?? []);
        Model = model;
        EndpointHost = endpointHost;

        if (qaInnerHandler is not null)
        {
            var config = new SparkOpenConfig("https://spark-open.fake.local/v1", "fake-key", model, endpointHost);
            _httpClient = new HttpClient(new SparkOpenChatHandler(config, qaInnerHandler));
            _metaClient = new MetaClient(_httpClient, config.ApiKey);
        }
    }

    public string Model { get; }

    public string EndpointHost { get; }

    /// <summary>Count of judge calls made, for asserting call caps and that a fail-closed path made none.</summary>
    public int JudgeCalls => _judgeCalls;

    public Task<MeaningCheckResult> JudgeMeaningAsync(
        string sourceText,
        string candidate,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _judgeCalls);
        JudgeCallsRecorded.Add((sourceText, candidate));

        if (_judgeReplies.Count == 0)
        {
            throw new InvalidOperationException("FakeSparkOpenClient has no more scripted judge replies.");
        }

        string raw = _judgeReplies.Dequeue();
        if (!SparkOpenReasoningParser.TryExtractFinalJsonObject(raw, out string finalAnswer, requireEndOfReply: true))
        {
            // Unextractable final answer is a failed judgement: Error, with the raw reply as context.
            return Task.FromResult(MeaningGate.ForError(
                "Spark Open reply carried no extractable final JSON answer.", Model, null, EndpointHost));
        }

        return Task.FromResult(SparkOpenClient.ParseStrictMeaningVerdict(finalAnswer, Model, null, EndpointHost));
    }

    public MetaClient AsMetaClient()
        => _metaClient ?? throw new InvalidOperationException("FakeSparkOpenClient was created without a QA-loop transport stub.");

    public void Dispose()
    {
        _metaClient?.Dispose();
        _httpClient?.Dispose();
    }
}
