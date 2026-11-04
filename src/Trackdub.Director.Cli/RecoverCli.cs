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

    private sealed class ParseState
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
        {
            ["--from"] = "en",
            ["--to"] = "es",
            ["--output"] = DefaultOutputFolder,
            ["--tolerance"] = QaOptions.Default.ToleranceFraction.ToString(CultureInfo.InvariantCulture),
        };

        public List<string> Reports { get; } = [];

        public bool ListOnly { get; set; }
    }

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
            return await RunSetAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSetAsync(Options options)
    {
        IReadOnlyList<RecoveryItem> set = await RecoverySetBuilder.BuildAsync(options.ReportPaths).ConfigureAwait(false);
        PrintSet(set);

        if (!IsSetSizeInRange(set))
        {
            return 1;
        }

        if (options.ListOnly)
        {
            Console.WriteLine($"--list-only: enumerated {set.Count} item(s); no run performed.");
            return 0;
        }

        // Checked before the route: a language mismatch is a usage error, not a route error.
        if (!IsSourceLanguageMatch(set, options))
        {
            return 1;
        }

        // Fail closed BEFORE constructing any client or making any call; name the missing vars.
        if (!IsRouteConfigured())
        {
            return 1;
        }

        return await RunRecoveryAsync(options, set).ConfigureAwait(false);
    }

    private static bool IsSetSizeInRange(IReadOnlyList<RecoveryItem> set)
    {
        if (set.Count >= MinSetSize && set.Count <= MaxSetSize)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"error: recovery set has {set.Count} item(s), outside the [{MinSetSize}, {MaxSetSize}] range; stopping without a run.");
        return false;
    }

    // The source language is intrinsic to the report text; a --from that disagrees with
    // the captured reports would judge the meaning gate against the wrong source language.
    private static bool IsSourceLanguageMatch(IReadOnlyList<RecoveryItem> set, Options options)
    {
        string? mismatchedSource = set.FirstOrDefault(
            i => !string.Equals(i.SourceLanguage, options.From, StringComparison.OrdinalIgnoreCase))?.SourceLanguage;
        if (mismatchedSource is null)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"error: recovery set has source language '{mismatchedSource}' but --from is '{options.From}'; source language cannot be overridden.");
        return false;
    }

    private static bool IsRouteConfigured()
    {
        string[] missing = MissingRouteVars();
        if (missing.Length == 0)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"error: Glimmer route not configured; no call was made. Missing: {string.Join(", ", missing)}.");
        return false;
    }

    private static async Task<int> RunRecoveryAsync(Options options, IReadOnlyList<RecoveryItem> set)
    {
        // The meaning gate cannot run without Meta credentials; fail closed BEFORE the paid
        // Glimmer probe, so a missing key never spends the Glimmer budget on a run that can
        // produce no recoveries.
        try
        {
            _ = MetaAuth.GetApiKey();
        }
        catch (MetaApiException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var glimmerBudget = new CallBudget(GlimmerCallCap);
        var metaBudget = new CallBudget(MetaCallCap);

        // Glimmer-30B on managed compute can take minutes per call under reasoning; a 120s HTTP
        // timeout is too tight for the loop (the probe succeeded in ~6s, but a full recovery turn
        // is much heavier). Give the loop calls generous headroom; the run cap still bounds spend.
        using var glimmerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        using var glimmer = new GlimmerClient(glimmerHttp);
        using var meta = new MetaClient();

        // The same budget-charged client is used for the probe, so the probe counts
        // toward the Glimmer call cap. Exactly ONE probe call; stop the run on any probe failure
        // before the capped loop begins. No Meta call is made during the probe. The service owns
        // charging the loop's Glimmer calls, so it receives the UNbudgeted client here.
        GlimmerClient budgetedGlimmer = glimmer.WithBudget(glimmerBudget);
        GlimmerProbeResult? probe = null;
        try
        {
            probe = await budgetedGlimmer.ProbeAsync().ConfigureAwait(false);
            Console.WriteLine("Probe OK:");
            Console.WriteLine($"  endpoint host : {probe.Host}");
            Console.WriteLine($"  requested model: {probe.RequestedModel}");
            Console.WriteLine($"  returned model : {(string.IsNullOrEmpty(probe.ReturnedModel) ? "(none reported)" : probe.ReturnedModel)}");
            Console.WriteLine($"  usage         : prompt={probe.Usage?.PromptTokens ?? 0} completion={probe.Usage?.CompletionTokens ?? 0} total={probe.Usage?.TotalTokens ?? 0}");
            Console.WriteLine($"  wall seconds  : {probe.WallSeconds:0.000}");
            Console.WriteLine($"  Glimmer calls used: {glimmerBudget.Used}/{glimmerBudget.Limit}");
        }
        catch (MetaApiException ex)
        {
            Console.Error.WriteLine($"error: Glimmer probe failed; stopping before the loop (loop not run). {ex.Message}");
            return 1;
        }

        var service = new RecoveryAgentService(glimmer, meta, options.From, options.To, options.Tolerance);
        RecoveryReport report = await service.RunAsync(set, glimmerBudget, metaBudget, probe).ConfigureAwait(false);

        string outputFolder = Path.GetFullPath(options.OutputFolder);
        await RecoveryOutputWriter.WriteReportAsync(outputFolder, report).ConfigureAwait(false);
        await RecoveryOutputWriter.WriteNotesAsync(outputFolder, report).ConfigureAwait(false);

        PrintSummary(report, options);
        if (report.RunError is not null)
        {
            // A mid-loop provider failure: the completed item reports were still persisted above.
            Console.Error.WriteLine($"error: recovery run stopped early after a provider failure: {report.RunError}");
            return 1;
        }

        return 0;
    }

    /// <summary>The route vars that are unset/blank, in a stable order; empty when the route is complete.</summary>
    private static string[] MissingRouteVars()
    {
        return new[] { GlimmerAuth.BaseUrlEnv, GlimmerAuth.ApiKeyEnv, GlimmerAuth.ModelEnv }
            .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
            .ToArray();
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

    private static void PrintSummary(RecoveryReport report, Options options)
    {
        string outputFolder = Path.GetFullPath(options.OutputFolder);
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
        var state = new ParseState();
        error = ParseArguments(args, state);
        if (error is not null)
        {
            return null;
        }

        if (!TryParseTolerance(state.Values["--tolerance"], out double tolerance))
        {
            error = "--tolerance must be a non-negative number (a fraction, e.g. 0.10).";
            return null;
        }

        string[] paths = state.Reports.Count > 0 ? state.Reports.ToArray() : DefaultReportPaths;
        return new Options(paths, state.Values["--output"], state.Values["--from"], state.Values["--to"], tolerance, state.ListOnly);
    }

    private static string? ParseArguments(string[] args, ParseState state)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--list-only")
            {
                state.ListOnly = true;
                continue;
            }

            if (args[i] == "--report" && i + 1 < args.Length)
            {
                state.Reports.Add(args[++i]);
                continue;
            }

            string? validation = ValidatePair(args, i);
            if (validation is not null)
            {
                return validation;
            }

            state.Values[args[i]] = args[++i];
        }

        return null;
    }

    private static bool TryParseTolerance(string raw, out double tolerance)
    {
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance)
            && double.IsFinite(tolerance)
            && tolerance >= 0;
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
        Console.WriteLine($"  {GlimmerAuth.BaseUrlEnv}    OpenAI-compatible chat completions endpoint (https required; http only for loopback).");
        Console.WriteLine($"  {GlimmerAuth.ApiKeyEnv}     Glimmer route API key (required).");
        Console.WriteLine($"  {GlimmerAuth.ModelEnv}       Glimmer model id (required).");
        Console.WriteLine($"  {GlimmerAuth.ReasoningEffortEnv}  Optional reasoning effort (low|medium|high; default low).");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)  Required for the meaning gate.");
    }
}
