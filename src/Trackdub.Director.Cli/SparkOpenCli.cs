using System.Globalization;
using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;

/// <summary>
/// spark-open: the opt-in Azure Muse Spark Open lane. Subcommands:
///   judge        — Spark Open judges the 75 captured candidates; reports agreement vs the hosted verdicts.
///   production   — Spark Open translates the six voa10 lines through the existing QA loop.
///   translation  — Spark Open translates the 40 eval lines; existing gates (DurationEstimator + hosted
///                  MeaningGate) grade them.
///   smoke        — one minimal Spark Open chat completion (Phase 2 only).
///
/// Every subcommand fails closed with zero calls when SPARKOPEN_BASE_URL / SPARKOPEN_API_KEY /
/// SPARKOPEN_MODEL are not all set. Spark Open is never a silent fallback for the hosted default.
/// </summary>
internal static class SparkOpenCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        string sub = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        return sub switch
        {
            "judge" => await RunJudgeAsync(rest).ConfigureAwait(false),
            "production" => await RunProductionAsync(rest).ConfigureAwait(false),
            "translation" => await RunTranslationAsync(rest).ConfigureAwait(false),
            "smoke" => await RunSmokeAsync(rest).ConfigureAwait(false),
            _ => UnknownSub(sub),
        };
    }

    private static int UnknownSub(string sub)
    {
        Console.Error.WriteLine($"Unknown spark-open subcommand '{sub}'. Expected 'judge', 'production', 'translation', or 'smoke'.");
        PrintHelp();
        return 1;
    }

    private static bool EnsureConfigured()
    {
        if (SparkOpenAuth.IsConfigured())
        {
            return true;
        }

        Console.Error.WriteLine($"error: Spark Open is not configured. Set {SparkOpenAuth.BaseUrlEnv}, {SparkOpenAuth.ApiKeyEnv}, and {SparkOpenAuth.ModelEnv} in the environment. No call was made.");
        return false;
    }

    private static async Task<int> RunJudgeAsync(string[] args)
    {
        var opts = ParseCommon(args, out string? error);
        if (opts is null)
        {
            Console.Error.WriteLine($"error: {error}");
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        try
        {
            IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(
                opts.CasesPath, opts.PressurePath, opts.UniformPaths).ConfigureAwait(false);
            Console.WriteLine($"Spark Open judge: {items.Count} items ({opts.From}->{opts.To}).");

            using var client = new SparkOpenClient();
            var runner = new SparkOpenJudgeRunner(client);
            JudgeAgreementReport report = await runner.RunAsync(items, opts.From, opts.To).ConfigureAwait(false);
            await runner.WriteReportAsync(opts.Output, report).ConfigureAwait(false);

            JudgeAgreementSummary s = report.Summary;
            Console.WriteLine($"  compared {s.Compared}/{s.TotalItems}: agree {s.Agree}, disagree {s.Disagree}, excluded {s.Excluded}, agreement {Percent(s.AgreementRate)}, tokens {s.TotalTokens}");
            Console.WriteLine($"Report written to {opts.Output}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SparkOpenException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunProductionAsync(string[] args)
    {
        string? input = GetValue(args, "--input");
        string? output = GetValue(args, "--output");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("error: --input <segments.json> and --output <report.json> are required.");
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        try
        {
            IReadOnlyList<QaSegmentInput> segments = await QaService.LoadSegmentsAsync(Path.GetFullPath(input)).ConfigureAwait(false);
            using var client = new SparkOpenClient();
            var runner = new SparkOpenProductionRunner(client, QaOptions.FromEnvironment());
            SparkOpenProductionReport report = await runner.RunAsync(segments, "en", "es").ConfigureAwait(false);
            await runner.WriteReportAsync(Path.GetFullPath(output), report).ConfigureAwait(false);

            Console.WriteLine($"Spark Open production: {report.QaReport.Totals.FinalPassCount}/{report.QaReport.Totals.TotalSegments} final pass, fully gate-verified={report.FullyGateVerified}, tokens {report.QaReport.Totals.TotalTokens}");
            Console.WriteLine($"Report written to {output}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SparkOpenException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTranslationAsync(string[] args)
    {
        string? input = GetValue(args, "--input");
        string? output = GetValue(args, "--output");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("error: --input <segments.json> and --output <report.json> are required.");
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        try
        {
            IReadOnlyList<QaSegmentInput> segments = await QaService.LoadSegmentsAsync(Path.GetFullPath(input)).ConfigureAwait(false);
            using var sparkOpen = new SparkOpenClient();
            using var hosted = new MetaClient();
            var runner = new SparkOpenTranslationRunner(sparkOpen, hosted, QaOptions.FromEnvironment());
            SparkOpenTranslationReport report = await runner.RunAsync(segments, "en", "es").ConfigureAwait(false);
            await runner.WriteReportAsync(Path.GetFullPath(output), report).ConfigureAwait(false);

            SparkOpenTranslationSummary s = report.Summary;
            Console.WriteLine($"Spark Open translation lane: timing {Percent(s.TimingPassRate)}, meaning {Percent(s.MeaningPassRate)}, combined {Percent(s.CombinedPassRate)} over {s.Lines} lines");
            Console.WriteLine($"Report written to {output}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SparkOpenException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSmokeAsync(string[] args)
    {
        string text = GetValue(args, "--text") ?? "Hello.";
        if (!EnsureConfigured())
        {
            return 2;
        }

        try
        {
            using var client = new SparkOpenClient();
            MetaClient meta = client.AsMetaClient();
            MetaClient.ChatCompletionResult result = await meta.ChatCompletionWithUsageAsync(
                new MetaClient.ChatRequest([new MetaClient.ChatMessage("user", $"Reply with the JSON object {{\"ok\":true}} on the final line. Context: {text}")])).ConfigureAwait(false);
            Console.WriteLine($"Spark Open smoke OK: model={result.Model}, usage total={result.Usage?.TotalTokens ?? 0}");
            return 0;
        }
        catch (Exception ex) when (ex is SparkOpenException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private sealed record CommonOptions(string CasesPath, string PressurePath, IReadOnlyList<string> UniformPaths, string Output, string From, string To);

    private static CommonOptions? ParseCommon(string[] args, out string? error)
    {
        string cases = GetValue(args, "--cases") ?? Path.Combine("samples", "meaning-eval", "cases.json");
        string pressure = GetValue(args, "--pressure") ?? Path.Combine("samples", "meaning-eval", "qa-report-pressure-70.json");
        string? output = GetValue(args, "--output");
        string from = GetValue(args, "--from") ?? "en";
        string to = GetValue(args, "--to") ?? "es";

        string[] uniform =
        [
            GetValue(args, "--uniform-scale") ?? Path.Combine("samples", "voa10-scale", "qa-report-scale.json"),
            GetValue(args, "--uniform-voa10") ?? Path.Combine("samples", "voa10-scale", "qa-report-voa10.json"),
        ];

        if (string.IsNullOrWhiteSpace(output))
        {
            error = "--output <report.json> is required.";
            return null;
        }

        error = null;
        return new CommonOptions(Path.GetFullPath(cases), Path.GetFullPath(pressure), uniform.Select(Path.GetFullPath).ToArray(), Path.GetFullPath(output), from, to);
    }

    private static string? GetValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string Percent(double? value) => value is null ? "-" : value.Value.ToString("P1", CultureInfo.InvariantCulture);

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director spark-open <judge|production|translation|smoke> [options]");
        Console.WriteLine();
        Console.WriteLine("Opt-in Azure Muse Spark Open lane. The hosted Meta Model API stays the default everywhere;");
        Console.WriteLine("Spark Open is never a silent fallback. Every subcommand fails closed with zero calls when");
        Console.WriteLine($"{SparkOpenAuth.BaseUrlEnv}, {SparkOpenAuth.ApiKeyEnv}, and {SparkOpenAuth.ModelEnv} are not all set.");
        Console.WriteLine();
        Console.WriteLine("  judge        Spark Open judges the 75 captured candidates; agreement vs hosted verdicts.");
        Console.WriteLine("               --output <report.json> (required); --cases/--pressure/--uniform-* override inputs.");
        Console.WriteLine("  production   Spark Open translates --input lines through the existing QA loop.");
        Console.WriteLine("               --input <segments.json> --output <report.json> (required).");
        Console.WriteLine("  translation  Spark Open translates --input lines; DurationEstimator + hosted MeaningGate grade.");
        Console.WriteLine("               --input <segments.json> --output <report.json> (required).");
        Console.WriteLine("  smoke        One minimal Spark Open chat completion. [--text <line>]");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine($"  {SparkOpenAuth.BaseUrlEnv}   OpenAI-compatible chat/completions endpoint of the Azure deployment.");
        Console.WriteLine($"  {SparkOpenAuth.ApiKeyEnv}    Deployment key. Never logged, printed, committed, or stored.");
        Console.WriteLine($"  {SparkOpenAuth.ModelEnv}      Deployment name Michael set.");
        Console.WriteLine("  translation also needs META_API_KEY: meaning grading uses the hosted gate (muse-spark-1.1).");
    }
}
