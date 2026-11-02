using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;
using static Trackdub.Director.Tests.SparkOpen.SparkOpenTestInfra;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>
/// The judge runner records verdicts and computes agreement ONLY over items both models judged.
/// An Error or unparseable Spark Open judgement, or a non-clean hosted verdict, is excluded and
/// counted — never treated as agreement.
/// </summary>
public sealed class SparkOpenJudgeRunnerTests
{
    private static JudgeItem Item(string id, MeaningVerdict hosted) => new(id, "test", "src " + id, "cand " + id, hosted);

    [Fact]
    public async Task Agreement_is_computed_only_over_items_both_models_judged()
    {
        JudgeItem[] items =
        [
            Item("agree-pass", MeaningVerdict.Pass),
            Item("agree-fail", MeaningVerdict.Fail),
            Item("disagree", MeaningVerdict.Pass),
            Item("spark-error", MeaningVerdict.Fail),
        ];

        // Scripted Spark Open replies, in item order:
        //  - pass verdict  -> agrees with hosted Pass
        //  - fail verdict  -> agrees with hosted Fail
        //  - fail verdict  -> disagrees with hosted Pass
        //  - no JSON verdict -> Error, excluded, NOT counted as agreement even though hosted said Fail
        using var client = new FakeSparkOpenClient(
        [
            ReasoningMeaningReply(pass: true),
            ReasoningMeaningReply(pass: false, "number", "number"),
            ReasoningMeaningReply(pass: false, "role swap", "role_swap"),
            "<think>still deliberating, no verdict emitted</think>",
        ]);

        JudgeAgreementReport report = await new SparkOpenJudgeRunner(client).RunAsync(items, "en", "es");
        JudgeAgreementSummary s = report.Summary;

        Assert.Equal(4, s.TotalItems);
        Assert.Equal(3, s.Compared);     // only the three clean-vs-clean items
        Assert.Equal(2, s.Agree);
        Assert.Equal(1, s.Disagree);
        Assert.Equal(1, s.Excluded);     // the Error item is counted as excluded
        Assert.Equal(2.0 / 3.0, s.AgreementRate!.Value, 4);

        JudgeItemResult errored = report.Results.Single(r => r.Id == "spark-error");
        Assert.Equal(JudgeComparison.Excluded, errored.Comparison);
        Assert.Equal(MeaningVerdict.Error, errored.SparkOpenVerdict);
    }

    [Fact]
    public async Task An_unparseable_spark_open_reply_is_excluded_not_a_disagreement()
    {
        JudgeItem[] items = [Item("x", MeaningVerdict.Pass)];
        using var client = new FakeSparkOpenClient(["this reply has no final json verdict at all"]);

        JudgeAgreementReport report = await new SparkOpenJudgeRunner(client).RunAsync(items, "en", "es");

        Assert.Equal(0, report.Summary.Compared);
        Assert.Equal(1, report.Summary.Excluded);
        Assert.Null(report.Summary.AgreementRate);
        Assert.Equal(JudgeComparison.Excluded, report.Results[0].Comparison);
    }

    [Fact]
    public void A_non_clean_hosted_verdict_is_excluded_even_when_spark_open_is_clean()
    {
        // Compare() must not score an item whose hosted side is not a clean Pass/Fail.
        var item = new JudgeItem("h", "test", "s", "c", MeaningVerdict.Error);
        var sparkOpenClean = new MeaningCheckResult(
            MeaningVerdict.Pass, [], "ok", FakeModel, MeaningGate.PromptTemplateVersion, DateTime.UtcNow.ToString("O"), null, null);

        JudgeItemResult result = SparkOpenJudgeRunner.Compare(item, sparkOpenClean);
        Assert.Equal(JudgeComparison.Excluded, result.Comparison);
    }

    [Fact]
    public async Task Agreement_rate_is_null_when_nothing_is_comparable()
    {
        JudgeItem[] items = [Item("a", MeaningVerdict.Pass), Item("b", MeaningVerdict.Fail)];
        using var client = new FakeSparkOpenClient(["no verdict one", "no verdict two"]);

        JudgeAgreementReport report = await new SparkOpenJudgeRunner(client).RunAsync(items, "en", "es");

        Assert.Equal(2, report.Summary.Excluded);
        Assert.Null(report.Summary.AgreementRate);
    }

    [Fact]
    public async Task Judge_call_cap_is_enforced_one_call_per_item()
    {
        JudgeItem[] items = [Item("a", MeaningVerdict.Pass), Item("b", MeaningVerdict.Fail), Item("c", MeaningVerdict.Pass)];
        using var client = new FakeSparkOpenClient(
        [
            ReasoningMeaningReply(pass: true),
            ReasoningMeaningReply(pass: false, "x", "x"),
            ReasoningMeaningReply(pass: true),
        ]);

        await new SparkOpenJudgeRunner(client).RunAsync(items, "en", "es");

        // Exactly one Spark Open judge call per item: the 75-item run bills 75 calls, no more.
        Assert.Equal(items.Length, client.JudgeCalls);
    }

    [Fact]
    public void Summarize_totals_tokens_across_all_results()
    {
        JudgeItemResult[] results =
        [
            new("a", "s", "t", "c", MeaningVerdict.Pass, null, MeaningVerdict.Pass, "ok", JudgeComparison.Agree, new MetaClient.TokenUsage(10, 20, 30)),
            new("b", "s", "t", "c", MeaningVerdict.Fail, null, MeaningVerdict.Pass, "x", JudgeComparison.Disagree, new MetaClient.TokenUsage(5, 5, 10)),
        ];

        JudgeAgreementSummary s = SparkOpenJudgeRunner.Summarize(results);
        Assert.Equal(40, s.TotalTokens);
        Assert.Equal(1, s.Agree);
        Assert.Equal(1, s.Disagree);
    }
}
