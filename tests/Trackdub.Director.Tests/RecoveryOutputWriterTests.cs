using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="RecoveryOutputWriter"/>: the recovery run's report and NOTES.md
/// writer. Exercises the real file writes in a temp folder; no model calls, no env reads.
/// </summary>
public sealed class RecoveryOutputWriterTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "trackdub-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static RecoveryReport Report(
        RecoveryProbeInfo? probe = null,
        string? runError = null,
        RecoveryItemReport[]? items = null)
    {
        return new RecoveryReport(
            SourceLanguage: "es",
            TargetLanguage: "en",
            GlimmerModel: "muse-glimmer-1",
            GlimmerReasoningEffort: "medium",
            MeaningModel: "muse-spark-1.1",
            MeaningPromptTemplateVersion: "meaning-v1",
            MaxAttemptsPerItem: 3,
            Probe: probe,
            RunError: runError,
            Items: items ?? [],
            Totals: new RecoveryTotals(
                TotalItems: 2,
                Recovered: 1,
                StillRefused: 1,
                DistinctLines: 2,
                DistinctLinesRecovered: 1,
                DistinctLinesStillRefused: 1,
                GlimmerCalls: 4,
                MetaCalls: 2,
                GlimmerCallCap: 20,
                MetaCallCap: 10,
                GlimmerTokens: GlimmerUsageRecord.Zero,
                MetaTokens: GlimmerUsageRecord.Zero));
    }

    private static RecoveryItemReport Item(string segmentId, RecoveryOutcome outcome, string? recoveredText)
    {
        return new RecoveryItemReport(
            SourceFile: "samples/source.json",
            SegmentId: segmentId,
            AttemptNumber: 1,
            SourceLine: "hola",
            WindowSeconds: 2.5,
            FailureType: RecoveryFailureType.Timing,
            Outcome: outcome,
            RecoveredText: recoveredText,
            Attempts:
            [
                new RecoveryAttemptTrace(
                    AttemptNumber: 1,
                    GlimmerAction: GlimmerActionKind.Propose,
                    MalformedReply: false,
                    ProposedText: "hola!",
                    Strategy: "shorten",
                    MeasuredSeconds: 2.1,
                    WindowSeconds: 2.5,
                    TimingVerdict: QaVerdict.Pass,
                    MeaningVerdict: MeaningVerdict.Pass,
                    MeaningIssues: null,
                    MeaningExplanation: null,
                    AttemptRecovered: true,
                    GlimmerUsage: GlimmerUsageRecord.Zero,
                    MeaningUsage: GlimmerUsageRecord.Zero),
            ]);
    }

    [Fact]
    public async Task WriteReportAsync_writes_indented_json_and_creates_the_folder()
    {
        // The folder does not exist yet: the writer must create it.
        await RecoveryOutputWriter.WriteReportAsync(_folder, Report());

        string path = Path.Combine(_folder, RecoveryOutputWriter.ReportFileName);
        Assert.True(File.Exists(path));

        string json = await File.ReadAllTextAsync(path);
        using JsonDocument doc = JsonDocument.Parse(json);

        // The report records carry explicit JsonPropertyName names (snake_case) for their consumers.
        Assert.Equal("es", doc.RootElement.GetProperty("source_language").GetString());
        Assert.Equal("muse-glimmer-1", doc.RootElement.GetProperty("glimmer_model").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("totals").GetProperty("total_items").GetInt32());
        Assert.Contains(Environment.NewLine, json);
    }

    [Fact]
    public async Task WriteReportAsync_leaves_no_temp_file_behind()
    {
        await RecoveryOutputWriter.WriteReportAsync(_folder, Report());

        Assert.Empty(Directory.GetFiles(_folder, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task WriteReportAsync_overwrites_a_previous_report()
    {
        await RecoveryOutputWriter.WriteReportAsync(_folder, Report());
        await RecoveryOutputWriter.WriteReportAsync(_folder, Report(items: [Item("seg-001", RecoveryOutcome.Recovered, "hola!")]));

        string json = await File.ReadAllTextAsync(Path.Combine(_folder, RecoveryOutputWriter.ReportFileName));
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal("seg-001", doc.RootElement.GetProperty("items")[0].GetProperty("segment_id").GetString());
    }

    [Fact]
    public async Task WriteReportAsync_cleans_up_its_temp_file_when_the_write_fails()
    {
        // A directory sitting at the destination makes the final move fail, which must
        // delete the staged temp file and rethrow rather than leave a partial artifact.
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(Path.Combine(_folder, RecoveryOutputWriter.ReportFileName));

        Exception error = await Record.ExceptionAsync(() => RecoveryOutputWriter.WriteReportAsync(_folder, Report()))
            ?? new InvalidOperationException("The write unexpectedly succeeded.");

        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected an IO failure, got {error.GetType().Name}: {error.Message}");
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WriteReportAsync_rejects_a_null_report()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RecoveryOutputWriter.WriteReportAsync(_folder, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteReportAsync_rejects_a_blank_folder(string folder)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => RecoveryOutputWriter.WriteReportAsync(folder, Report()));
    }

    [Fact]
    public async Task WriteNotesAsync_writes_the_run_summary()
    {
        await RecoveryOutputWriter.WriteNotesAsync(_folder, Report());

        string notes = await File.ReadAllTextAsync(Path.Combine(_folder, RecoveryOutputWriter.NotesFileName));

        Assert.Contains("# Glimmer recovery run notes", notes);
        Assert.Contains("- Direction: es -> en", notes);
        Assert.Contains("muse-glimmer-1", notes);
        Assert.Contains("muse-spark-1.1", notes);
        Assert.Contains("1/2 items recovered", notes);
        Assert.Contains("1/2 lines recovered", notes);
        Assert.Contains("Glimmer calls 4/20, Meta calls 2/10", notes);
        Assert.Contains("## Leakage rule", notes);
        Assert.Contains("## Per-item outcomes", notes);
        // No probe and no run error: neither optional section may appear.
        Assert.DoesNotContain("### Pre-flight probe", notes);
        Assert.DoesNotContain("## Run failure", notes);
    }

    [Fact]
    public async Task WriteNotesAsync_records_the_probe_and_a_run_failure()
    {
        var probe = new RecoveryProbeInfo(
            Host: "api.meta.ai",
            RequestedModel: "muse-glimmer-1",
            ReturnedModel: "muse-glimmer-1",
            PromptTokens: 12,
            CompletionTokens: 3,
            TotalTokens: 15,
            WallSeconds: 5.0);

        await RecoveryOutputWriter.WriteNotesAsync(_folder, Report(probe: probe, runError: "provider 502"));

        string notes = await File.ReadAllTextAsync(Path.Combine(_folder, RecoveryOutputWriter.NotesFileName));

        Assert.Contains("### Pre-flight probe", notes);
        Assert.Contains("api.meta.ai", notes);
        Assert.Contains("prompt=12 completion=3 total=15", notes);
        Assert.Contains("## Run failure", notes);
        Assert.Contains("provider 502", notes);
    }

    [Fact]
    public async Task WriteNotesAsync_lists_each_item_outcome_and_its_recovered_text()
    {
        await RecoveryOutputWriter.WriteNotesAsync(
            _folder,
            Report(items:
            [
                Item("seg-001", RecoveryOutcome.Recovered, "hola!"),
                Item("seg-002", RecoveryOutcome.ExhaustedAttempts, null),
            ]));

        string notes = await File.ReadAllTextAsync(Path.Combine(_folder, RecoveryOutputWriter.NotesFileName));

        Assert.Contains("`seg-001` att#1 (samples/source.json, Timing, window", notes);
        Assert.Contains("Recovered -> \"hola!\" [1 attempt(s)]", notes);
        Assert.Contains("`seg-002` att#1", notes);
        Assert.Contains("ExhaustedAttempts [1 attempt(s)]", notes);
        Assert.DoesNotContain("ExhaustedAttempts ->", notes);
    }

    [Fact]
    public async Task WriteNotesAsync_rejects_a_null_report()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RecoveryOutputWriter.WriteNotesAsync(_folder, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteNotesAsync_rejects_a_blank_folder(string folder)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => RecoveryOutputWriter.WriteNotesAsync(folder, Report()));
    }
}
