using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="RecoveryOutputWriter"/>: the recovery run's report and NOTES.md
/// writer. Exercises the real file writes in a temp folder; no model calls, no env reads.
/// </summary>
public sealed class RecoveryOutputWriterTests : IDisposable
{
    // Expected markers live in arrays so the test methods pass a list rather than a
    // long run of string arguments.
    private static readonly string[] SummaryMarkers =
    [
        "# Glimmer recovery run notes",
        "- Direction: es -> en",
        "muse-glimmer-1",
        "muse-spark-1.1",
        "1/2 items recovered",
        "1/2 lines recovered",
        "Glimmer calls 4/20, Meta calls 2/10",
        "## Leakage rule",
        "## Per-item outcomes",
    ];

    private static readonly string[] AbsentSectionMarkers =
    [
        // No probe and no run error: neither optional section may appear.
        "### Pre-flight probe",
        "## Run failure",
    ];

    private static readonly string[] ProbeMarkers =
    [
        "### Pre-flight probe",
        "api.meta.ai",
        "prompt=12 completion=3 total=15",
        "## Run failure",
        "provider 502",
    ];

    private static readonly string[] ItemMarkers =
    [
        "`seg-001` att#1 (samples/source.json, Timing, window",
        "Recovered -> \"hola!\" [1 attempt(s)]",
        "`seg-002` att#1",
        "ExhaustedAttempts [1 attempt(s)]",
    ];

    private static readonly string[] AbsentRecoveredMarkers =
    [
        // A refused item has no recovered text, so no arrow line for it.
        "ExhaustedAttempts ->",
    ];

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "trackdub-recovery-" + Guid.NewGuid().ToString("N"));

    private string ReportPath => Path.Combine(_folder, RecoveryOutputWriter.ReportFileName);

    private string NotesPath => Path.Combine(_folder, RecoveryOutputWriter.NotesFileName);

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

    /// <summary>Asserts that every marker appears in the written text.</summary>
    private static void ExpectMarkers(string text, IReadOnlyList<string> markers)
    {
        foreach (string marker in markers)
        {
            Assert.Contains(marker, text);
        }
    }

    /// <summary>Asserts that none of the markers appear in the written text.</summary>
    private static void ExpectNoMarkers(string text, IReadOnlyList<string> markers)
    {
        foreach (string marker in markers)
        {
            Assert.DoesNotContain(marker, text);
        }
    }

    /// <summary>The report header: required fields and their explicit snake_case wire names.</summary>
    private static void ExpectReportHeader(JsonDocument doc)
    {
        Assert.Equal("es", doc.RootElement.GetProperty("source_language").GetString());
        Assert.Equal("muse-glimmer-1", doc.RootElement.GetProperty("glimmer_model").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("totals").GetProperty("total_items").GetInt32());
    }

    [Fact]
    public async Task WriteReportAsync_writes_indented_json_and_creates_the_folder()
    {
        // The folder does not exist yet: the writer must create it.
        await RecoveryOutputWriter.WriteReportAsync(_folder, Report());

        Assert.True(File.Exists(ReportPath));

        string json = await File.ReadAllTextAsync(ReportPath);
        using JsonDocument doc = JsonDocument.Parse(json);

        ExpectReportHeader(doc);
        // WriteIndented emits a line break between members; LF is in the output on every platform.
        Assert.Contains("\n", json);
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

        string json = await File.ReadAllTextAsync(ReportPath);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal("seg-001", doc.RootElement.GetProperty("items")[0].GetProperty("segment_id").GetString());
    }

    [Fact]
    public async Task WriteReportAsync_cleans_up_its_temp_file_when_the_write_fails()
    {
        // A directory sitting at the destination makes the final move fail, which must
        // delete the staged temp file and rethrow rather than leave a partial artifact.
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(ReportPath);

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

        string notes = await File.ReadAllTextAsync(NotesPath);

        ExpectMarkers(notes, SummaryMarkers);
        ExpectNoMarkers(notes, AbsentSectionMarkers);
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

        string notes = await File.ReadAllTextAsync(NotesPath);

        ExpectMarkers(notes, ProbeMarkers);
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

        string notes = await File.ReadAllTextAsync(NotesPath);

        ExpectMarkers(notes, ItemMarkers);
        ExpectNoMarkers(notes, AbsentRecoveredMarkers);
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
