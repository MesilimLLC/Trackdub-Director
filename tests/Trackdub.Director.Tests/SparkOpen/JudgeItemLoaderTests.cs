using System.Text.Json;
using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>
/// The judge-item loader assembles exactly the 75 captured candidates from the project's own
/// evaluation sets on disk: 40 labeled cases + 23 pressure candidates + 12 uniform candidates, each
/// carrying the hosted verdict already recorded for it. Reads the real repo sample files.
/// </summary>
public sealed class JudgeItemLoaderTests
{
    private static string RepoRoot()
    {
        // tests/Trackdub.Director.Tests/bin/Debug/net10.0 -> repo root is five levels up.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Trackdub.Director.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repo root from the test base directory.");
    }

    private static (string Cases, string Pressure, string[] Uniform) Paths()
    {
        string root = RepoRoot();
        return (
            Path.Combine(root, "samples", "meaning-eval", "cases.json"),
            Path.Combine(root, "samples", "meaning-eval", "qa-report-pressure-70.json"),
            [
                Path.Combine(root, "samples", "voa10-scale", "qa-report-scale.json"),
                Path.Combine(root, "samples", "voa10-scale", "qa-report-voa10.json"),
            ]);
    }

    [Fact]
    public async Task Loads_exactly_75_items_split_40_23_12()
    {
        (string cases, string pressure, string[] uniform) = Paths();
        IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(cases, pressure, uniform);

        Assert.Equal(75, items.Count);
        Assert.Equal(40, items.Count(i => i.Source == JudgeItemLoader.CasesSource));
        Assert.Equal(23, items.Count(i => i.Source == JudgeItemLoader.PressureSource));
        Assert.Equal(12, items.Count(i => i.Source == JudgeItemLoader.UniformSource));
    }

    [Fact]
    public async Task Every_item_carries_a_clean_hosted_pass_or_fail_verdict_and_a_candidate()
    {
        (string cases, string pressure, string[] uniform) = Paths();
        IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(cases, pressure, uniform);

        // These are candidates the hosted model already judged: each must carry a clean verdict to diff.
        Assert.All(items, i =>
        {
            Assert.True(i.HostedVerdict is MeaningVerdict.Pass or MeaningVerdict.Fail, $"item {i.Id} has a non-clean hosted verdict");
            Assert.False(string.IsNullOrWhiteSpace(i.SourceText));
            Assert.False(string.IsNullOrWhiteSpace(i.Candidate));
        });
    }

    [Fact]
    public async Task Item_ids_are_unique()
    {
        (string cases, string pressure, string[] uniform) = Paths();
        IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(cases, pressure, uniform);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Pressure_and_uniform_items_exclude_timing_only_attempts()
    {
        // The pressure report has 36 attempts but only 23 were meaning-evaluated; the loader must drop
        // the 13 NotEvaluated (timing-only) attempts, which carry no hosted meaning verdict to diff.
        (string cases, string pressure, string[] uniform) = Paths();
        IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(cases, pressure, uniform);

        Assert.Equal(23, items.Count(i => i.Source == JudgeItemLoader.PressureSource));
        Assert.Equal(12, items.Count(i => i.Source == JudgeItemLoader.UniformSource));
    }

    [Fact]
    public async Task Cases_hosted_verdicts_match_the_recorded_meaning_eval_report()
    {
        // A mapping regression that swapped Pass/Fail for two cases would flip Agree/Disagree in the
        // judge report; each cases item must equal the hosted gate verdict recorded for its own id.
        (string cases, string pressure, string[] uniform) = Paths();
        IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(cases, pressure, uniform);

        string reportPath = Path.Combine(RepoRoot(), "samples", "meaning-eval", "meaning-eval-report.json");
        string json = await File.ReadAllTextAsync(reportPath);
        MeaningEvalReport? report = JsonSerializer.Deserialize<MeaningEvalReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(report);

        Dictionary<string, MeaningVerdict> recorded = report!.Results.ToDictionary(r => r.Id, r => r.Gate.Verdict, StringComparer.Ordinal);
        foreach (JudgeItem item in items.Where(i => i.Source == JudgeItemLoader.CasesSource))
        {
            Assert.True(recorded.TryGetValue(item.Id, out MeaningVerdict hosted), $"no recorded gate verdict for {item.Id}");
            Assert.Equal(hosted, item.HostedVerdict);
        }
    }

    [Fact]
    public async Task Repeated_uniform_report_paths_are_rejected_before_judging()
    {
        (string cases, string pressure, _) = Paths();
        string dup = Path.Combine(RepoRoot(), "samples", "voa10-scale", "qa-report-scale.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            JudgeItemLoader.LoadAsync(cases, pressure, [dup, dup]));
    }

    [Fact]
    public async Task A_report_missing_segments_fails_with_a_clear_error()
    {
        (string cases, _, _) = Paths();
        string temp = Path.Combine(Path.GetTempPath(), $"spark-judge-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(temp, "{\"source_language\":\"en\",\"target_language\":\"es\"}");
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                JudgeItemLoader.LoadAsync(cases, temp, []));
            Assert.Contains("segments", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
