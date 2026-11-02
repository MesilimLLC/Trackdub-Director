using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for the recovery-set enumeration and its leakage rule. Built from synthetic QA
/// report JSON (the same schema the captured reports use) so the rule is asserted without
/// depending on the on-disk sample files.
/// </summary>
public sealed class RecoverySetBuilderTests
{
    private static string ReportJson(params object[] segments)
    {
        var report = new
        {
            source_language = "en",
            target_language = "es",
            qa_options = new
            {
                max_attempts = 3,
                tolerance_fraction = 0.10,
                target_cps = 15.0,
                source_cps = 15.0,
                reasoning_effort = "low",
            },
            segments,
            totals = new
            {
                total_segments = segments.Length,
                first_pass_count = 0,
                final_pass_count = 0,
                total_revisions = 0,
                first_pass_rate = 0.0,
                final_pass_rate = 0.0,
                total_tokens = 0,
                prompt_tokens = 0,
                completion_tokens = 0,
            },
        };
        return JsonSerializer.Serialize(report);
    }

    private static object Attempt(
        int number, string translation, string timing, string meaning, double window = 1.0,
        double est = 0.5, string[]? issues = null, string? explanation = null)
    {
        string verdict = (timing == "Pass" && meaning == "Pass") ? "Pass" : "Fail";
        return new
        {
            attempt_number = number,
            kind = number == 1 ? "Initial" : "Revision",
            translation,
            estimated_seconds = est,
            window_seconds = window,
            max_chars = 20,
            verdict,
            prompt_tokens = 1,
            completion_tokens = 1,
            total_tokens = 2,
            model_id = "muse-spark-1.1",
            timing_verdict = timing,
            meaning_verdict = meaning,
            meaning_issues = issues,
            meaning_explanation = explanation,
        };
    }

    private static object Segment(string id, string text, string finalVerdict, params object[] attempts)
    {
        object first = attempts[0];
        object[] revisions = attempts.Skip(1).ToArray();
        return new
        {
            id,
            text,
            window_seconds = 1.0,
            first_attempt = first,
            revisions,
            final_verdict = finalVerdict,
            final_translation = text,
            token_usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
        };
    }

    [Fact]
    public void Meaning_failed_attempt_qualifies_even_when_the_segment_eventually_passed()
    {
        // Mirrors the voa10f-002 "¡Hola!" case: rev#2 fails meaning, rev#3 passes; segment final Pass.
        string json = ReportJson(Segment(
            "voa10f-002", "Hi Ashley!", "Pass",
            Attempt(1, "¡Hola, Ashley!", "Fail", "NotEvaluated", est: 0.93),
            Attempt(2, "¡Hola!", "Pass", "Fail", est: 0.40, issues: ["Named entity 'Ashley' omitted"], explanation: "Drops the addressee."),
            Attempt(3, "¡Ashley!", "Pass", "Pass", est: 0.53)));

        IReadOnlyList<RecoveryItem> items = RecoverySetBuilder.FromReportJson(json, "voa10-full/qa-report-pass1.json");

        RecoveryItem item = Assert.Single(items);
        Assert.Equal(("voa10f-002", 2, RecoveryFailureType.Meaning), (item.SegmentId, item.AttemptNumber, item.FailureType));
        Assert.Equal("¡Hola!", item.RefusedCandidate);
        Assert.Contains(item.GateFailureReasons, r => r.Contains("Ashley", StringComparison.Ordinal));
    }

    [Fact]
    public void Leakage_rule_excludes_every_other_attempt_text_for_the_same_segment()
    {
        // rev#3 "¡Ashley!" is the passing candidate and attempt 1 is the initial; neither must leak
        // into the item built from rev#2.
        string json = ReportJson(Segment(
            "voa10f-002", "Hi Ashley!", "Pass",
            Attempt(1, "¡Hola, Ashley!", "Fail", "NotEvaluated", est: 0.93),
            Attempt(2, "¡Hola!", "Pass", "Fail", est: 0.40, issues: ["Named entity 'Ashley' omitted"]),
            Attempt(3, "¡Ashley!", "Pass", "Pass", est: 0.53)));

        RecoveryItem item = Assert.Single(RecoverySetBuilder.FromReportJson(json, "voa10-full/qa-report-pass1.json"));

        // The item only ever references its own refused candidate and the source line.
        string whole = item.SourceLine + "|" + item.RefusedCandidate + "|" + string.Join("|", item.GateFailureReasons) + "|" + (item.SceneContext ?? "");
        Assert.DoesNotContain("¡Ashley!", whole, StringComparison.Ordinal);       // the accepted candidate
        Assert.DoesNotContain("¡Hola, Ashley!", whole, StringComparison.Ordinal); // the initial attempt
        Assert.Contains("¡Hola!", whole, StringComparison.Ordinal);               // its own refused candidate
    }

    [Fact]
    public void A_segment_with_two_meaning_failed_attempts_yields_two_items()
    {
        // Mirrors seg-006: rev#2 and rev#3 both fail meaning; rev#4 passes. Two qualifying attempts.
        string json = ReportJson(Segment(
            "seg-006", "Where are we shooting tomorrow?", "Pass",
            Attempt(1, "¿Dónde rodamos mañana?", "Fail", "NotEvaluated", est: 1.46),
            Attempt(2, "¿Rodamos mañana?", "Pass", "Fail", est: 1.06, issues: ["drops 'where'"]),
            Attempt(3, "¿Dónde rodamos?", "Pass", "Fail", est: 1.00, issues: ["drops 'tomorrow'"]),
            Attempt(4, "Dónde film mañana?", "Pass", "Pass", est: 1.20)));

        IReadOnlyList<RecoveryItem> items = RecoverySetBuilder.FromReportJson(json, "meaning-eval/qa-report-pressure-70.json");

        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { 2, 3 }, items.Select(i => i.AttemptNumber).ToArray());
        Assert.Equal(new[] { "¿Rodamos mañana?", "¿Dónde rodamos?" }, items.Select(i => i.RefusedCandidate).ToArray());
        // Neither item carries the accepted "Dónde film mañana?".
        Assert.All(items, i => Assert.DoesNotContain("Dónde film mañana?", i.RefusedCandidate, StringComparison.Ordinal));
    }

    [Fact]
    public void Fully_passing_segment_produces_no_items()
    {
        string json = ReportJson(Segment(
            "voa10-001", "Hi, Ashley.", "Pass",
            Attempt(1, "Hola, Ashley.", "Pass", "Pass", est: 0.80)));

        Assert.Empty(RecoverySetBuilder.FromReportJson(json, "voa10-scale/qa-report-voa10.json"));
    }

    [Fact]
    public void Timing_exhaustion_without_meaning_pass_qualifies_its_failed_attempts()
    {
        // A segment that never fit the window: final Fail, no meaning Pass anywhere.
        string json = ReportJson(Segment(
            "seg-x", "A line that cannot be compressed in time", "Fail",
            Attempt(1, "Una traducción larguísima que no cabe", "Fail", "NotEvaluated", est: 3.0),
            Attempt(2, "Otra traducción muy larga", "Fail", "NotEvaluated", est: 2.2)));

        IReadOnlyList<RecoveryItem> items = RecoverySetBuilder.FromReportJson(json, "synthetic/qa.json");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(RecoveryFailureType.Timing, i.FailureType));
        Assert.All(items, i => Assert.Contains(i.GateFailureReasons, r => r.Contains("Timing", StringComparison.Ordinal)));
    }

    [Fact]
    public void Item_carries_window_tolerance_and_target_cps_from_the_report()
    {
        string json = ReportJson(Segment(
            "seg-006", "Where are we shooting tomorrow?", "Pass",
            Attempt(1, "¿Dónde rodamos mañana?", "Fail", "NotEvaluated", est: 1.46, window: 1.12),
            Attempt(2, "¿Rodamos mañana?", "Pass", "Fail", est: 1.06, window: 1.12, issues: ["drops 'where'"])));

        RecoveryItem item = RecoverySetBuilder.FromReportJson(json, "meaning-eval/qa-report-pressure-70.json")[0];

        Assert.Equal(1.12, item.WindowSeconds);
        Assert.Equal(0.10, item.ToleranceFraction);
        Assert.Equal(15.0, item.TargetCharactersPerSecond);
        Assert.Equal(("en", "es"), (item.SourceLanguage, item.TargetLanguage));
    }
}
