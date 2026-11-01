using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class VerifyOnlyServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void Uncast_segment_is_human_review_required()
    {
        string runDir = CreateRun(BuildReport(cast: false, measured: 1.0, window: 1.2, meaning: MeaningVerdict.Pass));
        VerifyOnlyResult result = new VerifyOnlyService().VerifyRun(runDir);
        Assert.Equal(1, result.ExitCode);
        VerifyOnlySegmentReceipt seg = Assert.Single(result.Receipt.Segments);
        Assert.Equal(VerifyOnlySegmentVerdict.HumanReviewRequired, seg.Verdict);
        Assert.Contains(VerifyOnlyReason.Uncast, seg.Reasons);
    }

    [Fact]
    public void Timing_is_recomputed_and_summary_timing_verdict_is_ignored()
    {
        // Manifest claims timing Pass, but measured 3.0 vs window 2.0 at 10% fails.
        AuditionReport report = BuildReport(cast: true, measured: 3.0, window: 2.0, meaning: MeaningVerdict.Pass, timingVerdict: AuditionTimingVerdict.Pass);
        string runDir = CreateRun(report, writeMedia: true);
        VerifyOnlyResult result = new VerifyOnlyService().VerifyRun(runDir);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(VerifyOnlyReason.TimingFail, result.Receipt.Segments[0].Reasons);
        Assert.False(result.Receipt.Segments[0].TimingRecomputedPass);
    }

    [Fact]
    public void Meaning_not_evaluated_is_never_a_pass()
    {
        AuditionReport report = BuildReport(cast: true, measured: 1.0, window: 1.2, meaning: MeaningVerdict.NotEvaluated);
        string runDir = CreateRun(report, writeMedia: true);
        VerifyOnlyResult result = new VerifyOnlyService().VerifyRun(runDir);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(VerifyOnlyReason.MeaningNotEvaluated, result.Receipt.Segments[0].Reasons);
    }

    [Fact]
    public void Absent_media_uses_recorded_at_capture_and_requires_review()
    {
        AuditionReport report = BuildReport(cast: true, measured: 1.0, window: 1.2, meaning: MeaningVerdict.Pass);
        string runDir = CreateRun(report, writeMedia: false);
        File.WriteAllText(
            Path.Combine(runDir, VerifyOnlyService.MediaHashesFileName),
            """{"seg-1-take1.wav":"abc123"}""");

        VerifyOnlyResult result = new VerifyOnlyService().VerifyRun(runDir);
        Assert.Equal(1, result.ExitCode);
        VerifyOnlySegmentReceipt seg = result.Receipt.Segments[0];
        Assert.Contains(VerifyOnlyReason.MediaNotPresent, seg.Reasons);
        Assert.False(seg.MediaPresent);
        Assert.NotNull(seg.WinningMediaHash);
        Assert.Equal(VerifyOnlyHashProvenance.RecordedAtCapture, seg.WinningMediaHash!.Provenance);
        Assert.Equal("abc123", seg.WinningMediaHash.Sha256);
    }

    [Fact]
    public void Present_media_is_hashed_as_recomputed_and_verified()
    {
        AuditionReport report = BuildReport(cast: true, measured: 1.0, window: 1.2, meaning: MeaningVerdict.Pass);
        string runDir = CreateRun(report, writeMedia: true);
        VerifyOnlyResult result = new VerifyOnlyService().VerifyRun(runDir);
        Assert.Equal(0, result.ExitCode);
        VerifyOnlySegmentReceipt seg = Assert.Single(result.Receipt.Segments);
        Assert.Equal(VerifyOnlySegmentVerdict.Verified, seg.Verdict);
        Assert.True(seg.MediaPresent);
        Assert.Equal(VerifyOnlyHashProvenance.Recomputed, seg.WinningMediaHash!.Provenance);
        Assert.False(string.IsNullOrWhiteSpace(seg.WinningMediaHash.Sha256));
    }

    [Fact]
    public void Html_renders_from_receipt_json_alone()
    {
        AuditionReport report = BuildReport(cast: true, measured: 1.0, window: 1.2, meaning: MeaningVerdict.Pass);
        string runDir = CreateRun(report, writeMedia: true);
        VerifyOnlyReceipt receipt = new VerifyOnlyService().VerifyRun(runDir).Receipt;
        string json = JsonSerializer.Serialize(receipt, JsonOptions);
        string html = VerifyOnlyHtml.RenderFromJson(json);
        Assert.Contains("Hash-sealed receipt", html);
        Assert.Contains("recomputed", html);
        Assert.Contains("Hash-sealed", html);
        Assert.Contains("not a signature / certificate / attestation", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certified", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attested", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_run_dir_is_invalid_input()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new VerifyOnlyService().VerifyRun(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AuditionReport BuildReport(
        bool cast,
        double measured,
        double window,
        MeaningVerdict meaning,
        AuditionTimingVerdict timingVerdict = AuditionTimingVerdict.Pass)
    {
        var voice = new AuditionCastVoice("anna", "voice-1", "Alice");
        AuditionTakeReport? winner = null;
        if (cast)
        {
            winner = new AuditionTakeReport(
                TakeNumber: 1,
                Kind: QaAttemptKind.Initial,
                Text: "Hola.",
                MeaningVerdict: meaning,
                MeaningIssues: [],
                MeaningExplanation: "Greeting preserved.",
                Synthesized: true,
                MeasuredSeconds: measured,
                WindowSeconds: window,
                TimingVerdict: timingVerdict,
                IsWinner: true,
                AudioFileName: "seg-1-take1.wav",
                SynthesisNote: null,
                TranslationTokens: 1,
                MeaningTokens: 1,
                ElevenLabsCharacters: 5);
        }

        var takes = winner is null
            ? Array.Empty<AuditionTakeReport>()
            : new[] { winner };

        var segment = new AuditionSegmentReport(
            Id: "seg-1",
            Text: "Hi.",
            SpeakerId: "anna",
            CastVoice: voice,
            WindowSeconds: window,
            Takes: takes,
            Cast: cast,
            WinnerTakeNumber: cast ? 1 : null,
            WinnerFileName: cast ? "seg-1-take1.wav" : null);

        return new AuditionReport(
            SourceLanguage: "en",
            TargetLanguage: "es",
            Cast: [voice],
            Segments: [segment],
            Totals: new AuditionTotals(1, cast ? 1 : 0, cast ? 1 : 0, 0, 0, 0, 0, false, false));
    }

    private static string CreateRun(AuditionReport report, bool writeMedia = false)
    {
        string runDir = Path.Combine(Path.GetTempPath(), "verify-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, VerifyOnlyService.AuditionManifestFileName), JsonSerializer.Serialize(report, JsonOptions));
        if (writeMedia)
        {
            string auditionDir = Path.Combine(runDir, "media", "audition");
            Directory.CreateDirectory(auditionDir);
            foreach (AuditionSegmentReport segment in report.Segments)
            {
                foreach (AuditionTakeReport take in segment.Takes.Where(t => t.AudioFileName is not null))
                {
                    // Minimal valid-ish bytes for hashing presence; verify-only does not re-parse WAV for timing.
                    File.WriteAllBytes(Path.Combine(auditionDir, take.AudioFileName!), [1, 2, 3, 4, 5]);
                }
            }
        }

        return runDir;
    }
}
