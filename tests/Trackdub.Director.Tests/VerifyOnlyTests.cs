using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// verify-only gate: verdicts are recomputed from recorded numbers, a check that was not run is never a pass,
/// and a recorded hash is never labelled as recomputed.
/// </summary>
public sealed class VerifyOnlyTests : IDisposable
{
    private static readonly AuditionCastVoice Voice = new("anna", "voice-1", "Alice");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "verify-only-" + Guid.NewGuid().ToString("N"));

    public VerifyOnlyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static AuditionTakeReport Take(
        int number,
        double? measured,
        double window = 2.0,
        MeaningVerdict meaning = MeaningVerdict.Pass,
        AuditionTimingVerdict timing = AuditionTimingVerdict.Pass,
        bool winner = true,
        string? sha = null,
        string? text = null)
    {
        return new AuditionTakeReport(
            number,
            QaAttemptKind.Initial,
            text ?? $"Hola {number}.",
            meaning,
            [],
            "Meaning gate said so.",
            Synthesized: measured is not null,
            measured,
            window,
            timing,
            winner,
            AudioFileName: $"seg-take{number}.wav",
            SynthesisNote: null,
            TranslationTokens: 1,
            MeaningTokens: 1,
            ElevenLabsCharacters: 5,
            AudioSha256: sha,
            AudioBytes: [1, 2, 3, (byte)number]);
    }

    private static AuditionSegmentReport Segment(string id, double window, params AuditionTakeReport[] takes)
    {
        AuditionTakeReport? winner = takes.FirstOrDefault(t => t.IsWinner);
        return new AuditionSegmentReport(id, "Hello.", "anna", Voice, window, takes, winner is not null, winner?.TakeNumber, winner?.AudioFileName);
    }

    private static AuditionReport Report(params AuditionSegmentReport[] segments)
    {
        return new AuditionReport("en", "es", [Voice], segments, new AuditionTotals(segments.Length, 0, 0, 0, 0, 0, 0, false, false));
    }

    private string RunFolder(string name = "run")
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteManifest(string runDir, AuditionReport report)
    {
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(Path.Combine(runDir, AuditionOutputWriter.ManifestFileName), json);
    }

    private static void WriteMedia(string runDir, AuditionTakeReport take)
    {
        string dir = Path.Combine(runDir, "media", "audition");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, take.AudioFileName!), take.AudioBytes!);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // One segment with a single winning take; media written when asked.
    private VerifyReceipt VerifyOne(AuditionTakeReport take, double window = 2.0, bool media = true, double tolerance = 0.10)
    {
        string dir = RunFolder();
        WriteManifest(dir, Report(Segment("seg", window, take)));
        if (media)
        {
            WriteMedia(dir, take);
        }

        return VerifyOnlyService.Verify(dir, new VerifyOnlyOptions(ToleranceFraction: tolerance));
    }

    [Fact]
    public void A_line_with_a_winner_that_passes_every_check_is_verified()
    {
        AuditionTakeReport take = Take(1, measured: 2.1);

        VerifyReceipt receipt = VerifyOne(take);

        SegmentReceipt line = Assert.Single(receipt.Segments);
        Assert.Equal(SegmentVerdict.Verified, line.Verdict);
        Assert.Empty(line.Reasons);
        Assert.Equal(SegmentVerdict.Verified, receipt.Verdict);
        Assert.Equal(0, receipt.ExitStatus);
        Assert.Equal(Sha(take.AudioBytes!), line.Media!.Sha256);
        Assert.Equal(HashLabels.Recomputed, line.Media.HashLabel);
        Assert.Equal("Hola 1.", line.Spanish);
        Assert.Equal("Hello.", line.SourceLine);
        Assert.Equal(Voice, line.WinningVoice);
    }

    [Fact]
    public void Timing_is_recomputed_so_a_recorded_pass_cannot_hide_an_over_window_take()
    {
        // The manifest claims Pass, but 3.0 s against a 2.0 s window (+10%) does not fit.
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 3.0, timing: AuditionTimingVerdict.Pass));

        SegmentReceipt line = Assert.Single(receipt.Segments);
        Assert.Equal(SegmentVerdict.HumanReviewRequired, line.Verdict);
        Assert.Equal([VerifyReason.TimingFail], line.Reasons);
        Assert.Equal(AuditionTimingVerdict.Fail, line.Timing!.Verdict);
        Assert.Equal(1, receipt.ExitStatus);
    }

    [Fact]
    public void Timing_uses_the_same_tolerance_edge_as_the_qa_lane()
    {
        Assert.Equal(SegmentVerdict.Verified, VerifyOne(Take(1, measured: 2.2)).Segments[0].Verdict);
        Assert.Equal(SegmentVerdict.HumanReviewRequired, VerifyOne(Take(1, measured: 2.2001)).Segments[0].Verdict);
    }

    [Fact]
    public void A_winner_with_no_measurement_cannot_pass_timing()
    {
        VerifyReceipt receipt = VerifyOne(Take(1, measured: null));

        Assert.Contains(VerifyReason.TimingFail, receipt.Segments[0].Reasons);
    }

    [Theory]
    [InlineData(MeaningVerdict.Fail, VerifyReason.MeaningFail)]
    [InlineData(MeaningVerdict.Error, VerifyReason.MeaningFail)]
    [InlineData(MeaningVerdict.NotEvaluated, VerifyReason.MeaningNotEvaluated)]
    public void A_winner_without_a_recorded_meaning_pass_needs_review(MeaningVerdict recorded, VerifyReason expected)
    {
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 2.0, meaning: recorded));

        SegmentReceipt line = Assert.Single(receipt.Segments);
        Assert.Equal(SegmentVerdict.HumanReviewRequired, line.Verdict);
        Assert.Equal([expected], line.Reasons);
        Assert.Equal("Meaning gate said so.", line.Meaning!.Reason);
    }

    [Fact]
    public void A_line_with_no_winner_is_uncast_and_keeps_every_refusal()
    {
        string dir = RunFolder();
        AuditionTakeReport failed = Take(1, measured: 3.0, timing: AuditionTimingVerdict.Fail, winner: false);
        WriteManifest(dir, Report(Segment("seg", 2.0, failed)));

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        SegmentReceipt line = Assert.Single(receipt.Segments);
        Assert.Equal([VerifyReason.Uncast], line.Reasons);
        Assert.Null(line.Spanish);
        Assert.Null(line.WinningVoice);
        RefusedTake refusal = Assert.Single(line.Refusals);
        Assert.Equal([VerifyReason.TimingFail], refusal.Reasons);
        Assert.Contains("3.0000", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cast_flag_without_a_coherent_winner_take_is_still_uncast()
    {
        string dir = RunFolder();
        // cast=true and a winner number, but no take is flagged as the winner.
        AuditionSegmentReport lying = Segment("seg", 2.0, Take(1, measured: 2.0, winner: false)) with { Cast = true, WinnerTakeNumber = 1, WinnerFileName = "seg-take1.wav" };
        WriteManifest(dir, Report(lying));

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        Assert.Equal([VerifyReason.Uncast], receipt.Segments[0].Reasons);
    }

    [Fact]
    public void An_absent_media_file_is_review_required_and_never_labelled_recomputed()
    {
        string recorded = new('a', 64);
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 2.0, sha: recorded), media: false);

        SegmentReceipt line = Assert.Single(receipt.Segments);
        Assert.Equal(SegmentVerdict.HumanReviewRequired, line.Verdict);
        Assert.Equal([VerifyReason.MediaNotPresent], line.Reasons);
        Assert.False(line.Media!.Present);
        Assert.Equal(recorded, line.Media.Sha256);
        Assert.Equal(HashLabels.RecordedAtCapture, line.Media.HashLabel);
    }

    [Fact]
    public void An_absent_media_file_with_no_recorded_hash_reports_no_hash_rather_than_inventing_one()
    {
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 2.0), media: false);

        MediaCheck media = receipt.Segments[0].Media!;
        Assert.Null(media.Sha256);
        Assert.Equal(HashLabels.NotRecorded, media.HashLabel);
        Assert.NotEqual(HashLabels.Recomputed, media.HashLabel);
    }

    [Fact]
    public void A_sidecar_hash_labels_an_absent_file_recorded_in_sidecar_and_is_a_consumed_input()
    {
        string dir = RunFolder();
        AuditionTakeReport take = Take(1, measured: 2.0);
        WriteManifest(dir, Report(Segment("seg", 2.0, take)));
        string recorded = new('c', 64);
        File.WriteAllText(Path.Combine(dir, VerifyOnlyService.MediaHashSidecarFileName), $$$"""{"files":{"seg-take1.wav":"{{{recorded}}}"}}""");

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        MediaCheck media = receipt.Segments[0].Media!;
        Assert.Equal(recorded, media.Sha256);
        Assert.Equal(HashLabels.RecordedInSidecar, media.HashLabel);
        Assert.Equal([VerifyReason.MediaNotPresent], receipt.Segments[0].Reasons);
        Assert.Equal(2, receipt.Inputs.Length);
        Assert.Equal(VerifyOnlyService.MediaHashSidecarFileName, receipt.Inputs[1].Path);
    }

    [Fact]
    public void A_present_file_that_differs_from_the_sidecar_hash_is_flagged()
    {
        string dir = RunFolder();
        AuditionTakeReport take = Take(1, measured: 2.0);
        WriteManifest(dir, Report(Segment("seg", 2.0, take)));
        WriteMedia(dir, take);
        File.WriteAllText(Path.Combine(dir, VerifyOnlyService.MediaHashSidecarFileName), $$$"""{"files":{"seg-take1.wav":"{{{new string('d', 64)}}}"}}""");

        Assert.Equal([VerifyReason.MediaHashMismatch], VerifyOnlyService.Verify(dir).Segments[0].Reasons);
    }

    [Fact]
    public void A_present_file_that_differs_from_the_recorded_hash_is_flagged()
    {
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 2.0, sha: new string('b', 64)));

        SegmentReceipt line = receipt.Segments[0];
        Assert.Equal([VerifyReason.MediaHashMismatch], line.Reasons);
        Assert.Equal(HashLabels.Recomputed, line.Media!.HashLabel);
    }

    [Theory]
    [InlineData("../evil.wav")]
    [InlineData("sub/evil.wav")]
    [InlineData("C:evil.wav")]
    public void A_media_name_that_is_not_a_plain_file_name_is_treated_as_absent(string fileName)
    {
        string dir = RunFolder();
        AuditionTakeReport take = Take(1, measured: 2.0) with { AudioFileName = fileName };
        File.WriteAllBytes(Path.Combine(_root, "evil.wav"), [9]);
        WriteManifest(dir, Report(Segment("seg", 2.0, take) with { WinnerFileName = fileName }));

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        Assert.Equal([VerifyReason.MediaNotPresent], receipt.Segments[0].Reasons);
    }

    [Fact]
    public void Every_line_is_judged_and_one_failure_makes_the_whole_run_review_required()
    {
        string dir = RunFolder();
        AuditionTakeReport good = Take(1, measured: 2.0);
        AuditionTakeReport bad = Take(1, measured: 9.0);
        WriteManifest(dir, Report(Segment("a", 2.0, good), Segment("b", 2.0, bad)));
        WriteMedia(dir, good);

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        Assert.Equal(1, receipt.SegmentsVerified);
        Assert.Equal(2, receipt.SegmentsTotal);
        Assert.Equal(1, receipt.ExitStatus);
        Assert.Equal(SegmentVerdict.HumanReviewRequired, receipt.Verdict);
    }

    [Fact]
    public void The_manifest_is_listed_as_a_consumed_input_with_a_recomputed_hash()
    {
        string dir = RunFolder();
        WriteManifest(dir, Report(Segment("seg", 2.0, Take(1, measured: 2.0))));

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        InputHash input = Assert.Single(receipt.Inputs);
        Assert.Equal(AuditionOutputWriter.ManifestFileName, input.Path);
        Assert.Equal(Sha(File.ReadAllBytes(Path.Combine(dir, AuditionOutputWriter.ManifestFileName))), input.Sha256);
        Assert.Equal(HashLabels.Recomputed, input.Label);
    }

    [Fact]
    public void A_media_dir_override_is_used_for_the_presence_check()
    {
        string dir = RunFolder();
        AuditionTakeReport take = Take(1, measured: 2.0);
        WriteManifest(dir, Report(Segment("seg", 2.0, take)));
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, take.AudioFileName!), take.AudioBytes!);

        VerifyReceipt receipt = VerifyOnlyService.Verify(dir, new VerifyOnlyOptions(MediaDir: elsewhere));

        Assert.Equal(SegmentVerdict.Verified, receipt.Verdict);
    }

    [Fact]
    public async Task A_run_written_by_the_audition_writer_verifies_until_its_media_is_removed()
    {
        string dir = RunFolder();
        AuditionReport report = Report(Segment("seg", 2.0, Take(1, measured: 3.0, timing: AuditionTimingVerdict.Fail, winner: false), Take(2, measured: 2.0)));
        await AuditionOutputWriter.WriteAsync(report, dir);

        Assert.Equal(0, VerifyOnlyService.Verify(dir).ExitStatus);

        Directory.Delete(Path.Combine(dir, "media"), recursive: true);
        VerifyReceipt gone = VerifyOnlyService.Verify(dir);

        Assert.Equal(1, gone.ExitStatus);
        MediaCheck media = gone.Segments[0].Media!;
        Assert.Equal(HashLabels.RecordedAtCapture, media.HashLabel);
        Assert.Equal(Sha([1, 2, 3, 2]), media.Sha256);
        Assert.Equal([VerifyReason.MediaNotPresent], gone.Segments[0].Reasons);
    }

    [Fact]
    public void Unreadable_input_raises_an_input_error_instead_of_a_verdict()
    {
        string empty = RunFolder("empty");
        string garbage = RunFolder("garbage");
        File.WriteAllText(Path.Combine(garbage, AuditionOutputWriter.ManifestFileName), "{ not json");
        string noSegments = RunFolder("none");
        WriteManifest(noSegments, Report());

        Assert.Throws<VerifyInputException>(() => VerifyOnlyService.Verify(Path.Combine(_root, "missing")));
        Assert.Throws<VerifyInputException>(() => VerifyOnlyService.Verify(empty));
        Assert.Throws<VerifyInputException>(() => VerifyOnlyService.Verify(garbage));
        Assert.Throws<VerifyInputException>(() => VerifyOnlyService.Verify(noSegments));
    }

    [Fact]
    public void The_receipt_page_is_standalone_and_renders_from_the_receipt_json_alone()
    {
        string dir = RunFolder();
        AuditionTakeReport good = Take(1, measured: 2.0, text: "<b>Hola & adiós</b>");
        AuditionTakeReport lost = Take(1, measured: 9.0, winner: false, timing: AuditionTimingVerdict.Fail);
        WriteManifest(dir, Report(Segment("a", 2.0, good), Segment("b", 2.0, lost)));
        VerifyReceipt receipt = VerifyOnlyService.Verify(dir);

        string html = VerifyReceiptHtml.Render(VerifyReceipt.FromJson(receipt.ToJson()));

        Assert.Equal(VerifyReceiptHtml.Render(receipt), html);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Hola &amp; adi&#243;s&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("HumanReviewRequired", html, StringComparison.Ordinal);
        Assert.Contains("Exit status <strong>1</strong>", html, StringComparison.Ordinal);
        Assert.Contains(HashLabels.NotRecorded, html, StringComparison.Ordinal);
        Assert.Contains("Hash-sealed", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_text_never_claims_to_be_signed_certified_or_attested()
    {
        VerifyReceipt receipt = VerifyOne(Take(1, measured: 2.0));
        string text = receipt.ToJson() + VerifyReceiptHtml.Render(receipt);

        foreach (string word in new[] { "signed", "certif", "attest" })
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class RunsFactory(string runsRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VerifyOnly:RunsRoot", runsRoot);
        }
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_endpoint_returns_the_receipt_json_for_a_run_on_disk()
    {
        string dir = RunFolder("voa-test");
        AuditionTakeReport take = Take(1, measured: 2.0);
        WriteManifest(dir, Report(Segment("seg", 2.0, take)));
        WriteMedia(dir, take);
        using var factory = new RunsFactory(_root);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/verify-only", Body("""{"run":"voa-test"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        VerifyReceipt receipt = VerifyReceipt.FromJson(await response.Content.ReadAsStringAsync());
        Assert.Equal(SegmentVerdict.Verified, receipt.Verdict);
        Assert.Equal("voa-test", receipt.RunFolder);
        Assert.False(File.Exists(Path.Combine(dir, "verify-receipt.json")), "The endpoint must not write into the run folder.");
    }

    [Theory]
    [InlineData("""{"run":"../outside"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"run":"C:\\Windows"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"run":"/etc"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"run":""}""", HttpStatusCode.BadRequest)]
    [InlineData("""{}""", HttpStatusCode.BadRequest)]
    [InlineData("""not json""", HttpStatusCode.BadRequest)]
    [InlineData("""{"run":"no-such-run"}""", HttpStatusCode.NotFound)]
    public async Task The_endpoint_refuses_bad_or_escaping_run_names(string body, HttpStatusCode expected)
    {
        using var factory = new RunsFactory(_root);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/verify-only", Body(body));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_reports_an_unreadable_manifest_as_a_bad_request()
    {
        string dir = RunFolder("broken");
        File.WriteAllText(Path.Combine(dir, AuditionOutputWriter.ManifestFileName), "{ not json");
        using var factory = new RunsFactory(_root);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/verify-only", Body("""{"run":"broken"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
