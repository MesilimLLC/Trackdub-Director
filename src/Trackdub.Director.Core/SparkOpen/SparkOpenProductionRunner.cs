using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Runs Spark Open as the translation provider for the six voa10 demo-clip lines through the EXISTING
/// QA revision loop (<see cref="QaService"/>), exactly as a hosted production run is run — same timing
/// gate, same meaning gate, same bounce-back revisions. The only change is the provider behind the
/// loop: Spark Open instead of the hosted model.
///
/// It writes its own report to <c>samples/spark-open/spark-open-production.json</c> and never touches
/// the shipped voa10 translations, manifest, or media. Spark Open is opt-in here, never a silent
/// fallback; a Spark Open failure is a Spark Open failure, recorded as such.
/// </summary>
public sealed class SparkOpenProductionRunner
{
    public const string Stage = "spark-open-production";

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ISparkOpenClient _client;
    private readonly QaOptions _options;

    public SparkOpenProductionRunner(ISparkOpenClient client, QaOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? QaOptions.Default;
    }

    /// <summary>
    /// Runs the six voa10 lines through the QA loop with Spark Open as the provider. The returned
    /// <see cref="QaReport"/> is the existing QA report shape — model_id fields carry the Spark Open
    /// deployment name, so the report self-identifies as an open-model run.
    /// </summary>
    public async Task<SparkOpenProductionReport> RunAsync(
        IReadOnlyList<QaSegmentInput> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var qa = new QaService(_client.AsMetaClient(), _options);
        QaReport report = await qa.RunAsync(segments, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);

        // Meaning checks in this run were served by Spark Open (the redirected transport), but QaService
        // derives MeaningEndpointHost from MetaAuth; normalize the provenance so the report names the
        // actual endpoint, preserving null for attempts whose meaning check was never evaluated.
        QaReport normalized = NormalizeMeaningEndpointHost(report, _client.EndpointHost);

        bool fullyGateVerified = normalized.Segments.All(s => s.FinalVerdict == QaVerdict.Pass);
        return new SparkOpenProductionReport(
            Stage,
            nameof(TextProvider.SparkOpen),
            _client.Model,
            _client.EndpointHost,
            DateTime.UtcNow.ToString("O"),
            fullyGateVerified,
            normalized);
    }

    private static QaReport NormalizeMeaningEndpointHost(QaReport report, string sparkHost)
    {
        QaAttemptReport Fix(QaAttemptReport attempt)
            => attempt.MeaningEndpointHost is null ? attempt : attempt with { MeaningEndpointHost = sparkHost };

        var segments = report.Segments.Select(segment => segment with
        {
            // A failed generation (generation_error) has no attempts and therefore no meaning host.
            FirstAttempt = segment.FirstAttempt is null ? null : Fix(segment.FirstAttempt),
            Revisions = segment.Revisions.Select(Fix).ToArray(),
        }).ToArray();

        return report with { Segments = segments };
    }

    public async Task WriteReportAsync(string outputPath, SparkOpenProductionReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, WriteOptions), cancellationToken).ConfigureAwait(false);
    }
}
