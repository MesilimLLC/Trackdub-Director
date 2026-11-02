using System.Globalization;
using Trackdub.Director.Core;

/// <summary>
/// recover: run the Glimmer recovery agent over the set of refused candidates enumerated
/// from captured QA reports, and write recovery-report.json + NOTES.md into the output
/// folder. Glimmer PROPOSES; the existing gates (DurationEstimator timing, then
/// MeaningGate via MetaClient.CheckMeaningAsync) DISPOSE. Fails closed with ZERO calls
/// when the Glimmer route (GLIMMER_BASE_URL / GLIMMER_API_KEY / GLIMMER_MODEL) is unset.
/// Exit 0 = run completed, 1 = fail-closed / usage / IO error.
/// </summary>
internal static class RecoverCli
{
    // The five captured QA reports the recovery set is enumerated from (repo-relative).
    private static readonly string[] DefaultReportPaths =
    [
        "samples/voa10-scale/qa-report-voa10.json",
        "samples/voa10-scale/qa-report-scale.json",
        "samples/voa10-full/qa-report-pass1.json",
        "samples/voa10-full/qa-report-pass2.json",
        "samples/meaning-eval/qa-report-pressure-70.json",
    ];

    private const string DefaultOutputFolder = "samples/glimmer-recovery";
    private const int GlimmerCallCap = 45;
    private const int MetaCallCap = 35;
    private const int MinSetSize = 4;
    private const int MaxSetSize = 15;

    private sealed record Options(string[] ReportPaths, string OutputFolder, string From, string To, double Tolerance, bool ListOnly);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        Options? options = Parse(args, out string? error);
        if (options is null)
        {
            Console.Error.WriteLine($"error: {error}");
            PrintHelp();
            return 1;
        }

        try
        {
            IReadOnlyList<RecoveryItem> set = await RecoverySetBuilder.BuildAsync(options.ReportPaths).ConfigureAwait(false);
            PrintSet(set);

            if (set.Count < MinSetSize || set.Count > MaxSetSize)
            {
                Console.Error.WriteLine(
                    $"error: recovery set has {set.Count} item(s), outside the [{MinSetSize}, {MaxSetSize}] range; stopping without a run.");
                return 1;
            }

            if (options.ListOnly)
            {
                Console.WriteLine($"--list-only: enumerated {set.Count} item(s); no run performed.");
                return 0;
            }

            // Fail closed BEFORE constructing any client or making any call.
            if (!GlimmerAuth.IsConfigured())
            {
                Console.Error.WriteLine(
                    $"error: Glimmer route not configured; no call was made. Set {GlimmerAuth.BaseUrlEnv}, {GlimmerAuth.ApiKeyEnv}, and {GlimmerAuth.ModelEnv}.");
                return 1;
            }

            return await RunRecoveryAsync(options, set).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRecoveryAsync(Options options, IReadOnlyList<RecoveryItem> set)
    {
        var glimmerBudget = new CallBudget(GlimmerCallCap);
        var metaBudget = new CallBudget(MetaCallCap);

        using var glimmerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var glimmer = new GlimmerClient(glimmerHttp);
        using var meta = new MetaClient();

        var service = new RecoveryAgentService(glimmer.WithBudget(glimmerBudget), meta, options.From, options.To, options.Tolerance);
        RecoveryReport report = await service.RunAsync(set, glimmerBudget, metaBudget).ConfigureAwait(false);

        string outputFolder = Path.GetFullPath(options.OutputFolder);
        await RecoveryOutputWriter.WriteReportAsync(outputFolder, report).ConfigureAwait(false);
        await RecoveryOutputWriter.WriteNotesAsync(outputFolder, report).ConfigureAwait(false);

        PrintSummary(report, outputFolder);
        return 0;
    }

    private static void PrintSet(IReadOnlyList<RecoveryItem> set)
    {
        Console.WriteLine($"Recovery set: {set.Count} item(s)");
        foreach (RecoveryItem item in set)
        {
            Console.WriteLine(
                $"  {item.SourceFile} | {item.SegmentId} att#{item.AttemptNumber} | " +
                $"window {item.WindowSeconds:0.00}s | {item.FailureType} | \"{item.SourceLine}\"");
        }
    }

    private static void PrintSummary(RecoveryReport report, string outputFolder)
    {
        Console.WriteLine(
            $"Recovery run: {report.Totals.Recovered}/{report.Totals.TotalItems} attempts recovered, " +
            $"{report.Totals.DistinctLinesRecovered}/{report.Totals.DistinctLines} distinct lines recovered " +
            $"(Glimmer {report.Totals.GlimmerCalls}/{report.Totals.GlimmerCallCap} calls, " +
            $"Meta {report.Totals.MetaCalls}/{report.Totals.MetaCallCap} calls).");
        Console.WriteLine($"Report: {Path.Combine(outputFolder, RecoveryOutputWriter.ReportFileName)}");
        Console.WriteLine($"Notes:  {Path.Combine(outputFolder, RecoveryOutputWriter.NotesFileName)}");
    }

    private static Options? Parse(string[] args, out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--from"] = "en",
            ["--to"] = "es",
            ["--output"] = DefaultOutputFolder,
            ["--tolerance"] = QaOptions.Default.ToleranceFraction.ToString(CultureInfo.InvariantCulture),
        };
        bool listOnly = false;
        var reports = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--list-only")
            {
                listOnly = true;
                continue;
            }

            if (args[i] == "--report" && i + 1 < args.Length)
            {
                reports.Add(args[++i]);
                continue;
            }

            string? validation = ValidatePair(args, i);
            if (validation is not null)
            {
                error = validation;
                return null;
            }

            values[args[i]] = args[++i];
        }

        if (!double.TryParse(values["--tolerance"], NumberStyles.Float, CultureInfo.InvariantCulture, out double tolerance)
            || !double.IsFinite(tolerance) || tolerance < 0)
        {
            error = "--tolerance must be a non-negative number (a fraction, e.g. 0.10).";
            return null;
        }

        error = null;
        string[] paths = reports.Count > 0 ? reports.ToArray() : DefaultReportPaths;
        return new Options(paths, values["--output"], values["--from"], values["--to"], tolerance, listOnly);
    }

    private static string? ValidatePair(string[] args, int i)
    {
        string[] known = ["--from", "--to", "--output", "--tolerance", "--report"];
        string? value = i + 1 < args.Length ? args[i + 1] : null;
        return !known.Contains(args[i]) ? $"Unexpected argument '{args[i]}' for 'recover'."
            : string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? $"Missing value for '{args[i]}'."
            : null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director recover [--report <qa-report.json> ...] [--output <folder>] [--from en] [--to es] [--tolerance 0.10] [--list-only]");
        Console.WriteLine();
        Console.WriteLine("Glimmer recovery agent over refused candidates enumerated from captured QA reports.");
        Console.WriteLine("Glimmer proposes; the existing gates dispose (DurationEstimator timing, then the meaning gate).");
        Console.WriteLine("A line is recovered only when a proposal passes BOTH gates. Writes recovery-report.json and NOTES.md.");
        Console.WriteLine();
        Console.WriteLine("  --report <path>    A QA report to enumerate from (repeatable). Default: the five captured reports.");
        Console.WriteLine($"  --output <folder>  Output folder. Default: {DefaultOutputFolder}");
        Console.WriteLine("  --from <code>      Source language code. Default: en");
        Console.WriteLine("  --to <code>        Target language code. Default: es");
        Console.WriteLine("  --tolerance <f>    Timing tolerance fraction. Default: 0.10");
        Console.WriteLine("  --list-only        Enumerate and print the recovery set; make no calls.");
        Console.WriteLine();
        Console.WriteLine($"The set must hold between {MinSetSize} and {MaxSetSize} items or the run stops after the list.");
        Console.WriteLine($"Run caps: {GlimmerCallCap} Glimmer calls, {MetaCallCap} hosted Meta calls.");
        Console.WriteLine();
        Console.WriteLine("Environment (route read ONLY from env; the key is never logged, printed, committed, or stored):");
        Console.WriteLine($"  {GlimmerAuth.BaseUrlEnv}    OpenAI-compatible chat completions endpoint (required).");
        Console.WriteLine($"  {GlimmerAuth.ApiKeyEnv}     Glimmer route API key (required).");
        Console.WriteLine($"  {GlimmerAuth.ModelEnv}       Glimmer model id (required).");
        Console.WriteLine($"  {GlimmerAuth.ReasoningEffortEnv}  Optional reasoning effort (low|medium|high; default low).");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)  Required for the meaning gate.");
    }
}
