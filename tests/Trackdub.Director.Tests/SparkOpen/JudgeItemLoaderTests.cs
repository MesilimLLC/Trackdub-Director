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
}
