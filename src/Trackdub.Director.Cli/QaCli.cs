using System.Globalization;
using System.Text.Json;
using Trackdub.Director.Core;

internal static class QaCli
{
    private enum QaOption
    {
        Input,
        Output,
        From,
        To,
        MaxAttempts,
        Tolerance,
        Cps,
    }

    private static class QaOptionFlags
    {
        private static readonly IReadOnlyDictionary<QaOption, string> Flags = new Dictionary<QaOption, string>
        {
            [QaOption.Input] = "--input",
            [QaOption.Output] = "--output",
            [QaOption.From] = "--from",
            [QaOption.To] = "--to",
            [QaOption.MaxAttempts] = "--max-attempts",
            [QaOption.Tolerance] = "--tolerance",
            [QaOption.Cps] = "--cps",
        };

        private static readonly IReadOnlyDictionary<string, QaOption> ByFlag =
            Flags.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

        public static string ToFlag(QaOption option) => Flags[option];

        public static bool TryGet(string flag, out QaOption option) => ByFlag.TryGetValue(flag, out option);
    }

    private sealed class QaArguments
    {
        public string? Input { get; set; }

        public string? Output { get; set; }

        public string From { get; set; } = "en";

        public string To { get; set; } = "es";

        public int MaxAttempts { get; set; } = 3;

        public double Tolerance { get; set; } = 0.10;

        public double Cps { get; set; } = DurationEstimator.DefaultCharactersPerSecond;

        public bool HasMaxAttempts { get; set; }

        public bool HasTolerance { get; set; }

        public bool HasCps { get; set; }

        public bool TryApply(QaOption option, OptionValue value, out ParseError? error)
        {
            error = null;
            switch (option)
            {
                case QaOption.Input:
                    Input = value.Text;
                    return true;
                case QaOption.Output:
                    Output = value.Text;
                    return true;
                case QaOption.From:
                    From = value.Text;
                    return true;
                case QaOption.To:
                    To = value.Text;
                    return true;
                default:
                    return TryApplyNumeric(option, value, out error);
            }
        }

        private bool TryApplyNumeric(QaOption option, OptionValue value, out ParseError? error)
        {
            switch (option)
            {
                case QaOption.MaxAttempts:
                    return TryApplyMaxAttempts(value, out error);
                case QaOption.Tolerance:
                case QaOption.Cps:
                    return TryApplyPositiveNumber(option, value, out error);
                default:
                    error = new ParseError($"Unknown option '{QaOptionFlags.ToFlag(option)}' for 'qa'.");
                    return false;
            }
        }

        private bool TryApplyMaxAttempts(OptionValue value, out ParseError? error)
        {
            if (int.TryParse(value.Text, out int attempts) && attempts >= 0)
            {
                MaxAttempts = attempts;
                HasMaxAttempts = true;
                error = null;
                return true;
            }

            error = new ParseError("error: --max-attempts must be a non-negative integer.");
            return false;
        }

        private bool TryApplyPositiveNumber(QaOption option, OptionValue value, out ParseError? error)
        {
            if (!double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                error = InvalidNumber(option);
                return false;
            }

            bool isCps = option == QaOption.Cps;
            bool valid = isCps ? number > 0 : number >= 0;
            if (!valid)
            {
                error = InvalidNumber(option);
                return false;
            }

            if (isCps)
            {
                Cps = number;
                HasCps = true;
            }
            else
            {
                Tolerance = number;
                HasTolerance = true;
            }

            error = null;
            return true;
        }

        private static ParseError InvalidNumber(QaOption option) => option switch
        {
            QaOption.MaxAttempts => new ParseError("error: --max-attempts must be a non-negative integer."),
            QaOption.Tolerance => new ParseError("error: --tolerance must be a non-negative number (fraction, e.g. 0.10 for 10%)."),
            _ => new ParseError("error: --cps must be a positive number (characters per second)."),
        };
    }

    public static async Task<int> RunAsync(string[] args)
    {
        ParseResult<QaArguments> parsed = Parse(new CommandArgs(args));
        if (parsed.Status == ParseStatus.Help)
        {
            PrintHelp();
            return 0;
        }

        if (parsed.Status == ParseStatus.Error)
        {
            Console.Error.WriteLine(parsed.Error!.Value);
            PrintHelp();
            return 1;
        }

        return await RunParsedAsync(parsed.Value!).ConfigureAwait(false);
    }

    private static ParseResult<QaArguments> Parse(CommandArgs args)
    {
        var parsed = new QaArguments();
        IReadOnlyList<string> values = args.Values;
        for (int i = 0; i < values.Count; i++)
        {
            string token = values[i];
            if (token is "-h" or "--help")
            {
                return ParseResult<QaArguments>.Help();
            }

            if (i + 1 >= values.Count || !QaOptionFlags.TryGet(token, out QaOption option))
            {
                return ParseResult<QaArguments>.Failure(new ParseError($"Unknown option '{token}' for 'qa'."));
            }

            if (!parsed.TryApply(option, new OptionValue(values[i + 1]), out ParseError? error))
            {
                return ParseResult<QaArguments>.Failure(error!);
            }

            i++;
        }

        return ParseResult<QaArguments>.Success(parsed);
    }

    private static async Task<int> RunParsedAsync(QaArguments parsed)
    {
        if (!TryResolvePaths(parsed, out FilePath input, out FilePath output))
        {
            return 1;
        }

        QaOptions options = BuildOptions(parsed);
        try
        {
            // Pre-flight auth: a missing hosted key is a configuration error, not a per-segment
            // generation failure — fail the run up front rather than reporting every segment as failed.
            _ = MetaAuth.GetApiKey();

            return await ExecuteAsync(parsed, options, input, output).ConfigureAwait(false);
        }
        catch (MetaApiException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static bool TryResolvePaths(QaArguments parsed, out FilePath input, out FilePath output)
    {
        input = new FilePath(string.Empty);
        output = new FilePath(string.Empty);

        if (string.IsNullOrWhiteSpace(parsed.Input))
        {
            Console.Error.WriteLine("error: --input <segments.json> is required.");
            PrintHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Output))
        {
            Console.Error.WriteLine("error: --output <report.json> is required.");
            PrintHelp();
            return false;
        }

        input = new FilePath(parsed.Input);
        output = new FilePath(parsed.Output);

        if (!File.Exists(input.FullPath))
        {
            Console.Error.WriteLine($"error: input file not found: {input.FullPath}");
            return false;
        }

        return true;
    }

    private static QaOptions BuildOptions(QaArguments parsed)
    {
        // Build options: CLI overrides env, env overrides defaults
        QaOptions envOptions = QaOptions.FromEnvironment();
        var cpsOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in envOptions.CharactersPerSecondByLanguage)
        {
            cpsOverrides[kv.Key] = kv.Value;
        }

        if (parsed.HasCps)
        {
            cpsOverrides[parsed.To.Trim().ToLowerInvariant()] = parsed.Cps;
        }

        return new QaOptions
        {
            MaxAttempts = parsed.HasMaxAttempts ? parsed.MaxAttempts : envOptions.MaxAttempts,
            ToleranceFraction = parsed.HasTolerance ? parsed.Tolerance : envOptions.ToleranceFraction,
            CharactersPerSecondByLanguage = cpsOverrides,
            ReasoningEffort = envOptions.ReasoningEffort,
        };
    }

    private static async Task<int> ExecuteAsync(QaArguments parsed, QaOptions options, FilePath input, FilePath output)
    {
        IReadOnlyList<QaSegmentInput> segments = await QaService.LoadSegmentsAsync(input.FullPath).ConfigureAwait(false);
        Console.WriteLine($"QA: {segments.Count} segment(s) {parsed.From}->{parsed.To} | max-attempts={options.MaxAttempts} tolerance={options.ToleranceFraction:P0} cps={options.GetCharactersPerSecond(parsed.To):0.#} reasoning={options.ReasoningEffort ?? "(default)"}");

        using var client = new MetaClient();
        var service = new QaService(client, options);
        QaReport report = await service.RunAsync(segments, parsed.From, parsed.To).ConfigureAwait(false);
        await service.WriteReportAsync(output.FullPath, report).ConfigureAwait(false);

        PrintSummary(report);
        Console.WriteLine($"Report written to {output.FullPath}");
        return 0;
    }

    public static void PrintSummary(QaReport report)
    {
        Console.WriteLine($"Segments: {report.Totals.TotalSegments} | First-pass: {report.Totals.FirstPassCount}/{report.Totals.TotalSegments} ({report.Totals.FirstPassRate:P0}) | Final: {report.Totals.FinalPassCount}/{report.Totals.TotalSegments} ({report.Totals.FinalPassRate:P0}) | Revisions: {report.Totals.TotalRevisions} | Failed: {report.Totals.FailedSegments} | Tokens: {report.Totals.TotalTokens} (prompt {report.Totals.TotalPromptTokens} + completion {report.Totals.TotalCompletionTokens})");
        foreach (QaSegmentReport seg in report.Segments)
        {
            if (seg.FirstAttempt is null)
            {
                Console.WriteLine($"  {seg.Id}: FAILED (generation error: {seg.GenerationError ?? "unknown"}) | tokens {seg.TokenUsage.TotalTokens}");
                continue;
            }

            string flag = LowerVerdict(seg.FinalVerdict == QaVerdict.Pass).ToUpperInvariant();
            string firstFlag = LowerVerdict(seg.FirstAttempt.Verdict == QaVerdict.Pass);
            string firstTiming = LowerVerdict(seg.FirstAttempt.TimingVerdict == QaVerdict.Pass);
            string firstMeaning = MeaningLabel(seg.FirstAttempt.MeaningVerdict);
            Console.WriteLine($"  {seg.Id}: window {seg.WindowSeconds:0.00}s | first {firstFlag} (timing {firstTiming} meaning {firstMeaning} {seg.FirstAttempt.EstimatedSeconds:0.00}s, {seg.FirstAttempt.Translation.Length}c) | final {flag} ({seg.FinalTranslation!.Length}c, {seg.TokenUsage.TotalTokens} tok) | revisions {seg.Revisions.Length}");
            PrintFirstMeaning(seg.FirstAttempt);
            PrintRevisions(seg.Revisions);
        }
    }

    private static void PrintFirstMeaning(QaAttemptReport attempt)
    {
        if (HasMeaningIssues(attempt.MeaningIssues))
        {
            Console.WriteLine($"    first meaning issues: {string.Join("; ", attempt.MeaningIssues)}");
            if (!string.IsNullOrWhiteSpace(attempt.MeaningExplanation))
            {
                Console.WriteLine($"    first meaning explanation: {attempt.MeaningExplanation}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(attempt.MeaningExplanation) && attempt.MeaningVerdict == MeaningVerdict.Error)
        {
            Console.WriteLine($"    first meaning error: {attempt.MeaningExplanation}");
        }
    }

    private static void PrintRevisions(QaAttemptReport[] revisions)
    {
        foreach (QaAttemptReport rev in revisions)
        {
            string timing = LowerVerdict(rev.TimingVerdict == QaVerdict.Pass);
            string meaning = MeaningLabel(rev.MeaningVerdict);
            Console.WriteLine($"    rev #{rev.AttemptNumber}: {rev.Verdict.ToString().ToLowerInvariant()} (timing {timing} meaning {meaning}) {rev.EstimatedSeconds:0.00}s \"{Trunc(rev.Translation, 64)}\" meanTok {rev.MeaningTotalTokens}");
            if (HasMeaningIssues(rev.MeaningIssues))
            {
                Console.WriteLine($"      meaning issues: {string.Join("; ", rev.MeaningIssues)}");
            }

            if (!string.IsNullOrWhiteSpace(rev.MeaningExplanation))
            {
                Console.WriteLine($"      meaning: {rev.MeaningExplanation}");
            }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

    private static string LowerVerdict(bool pass) => pass ? "pass" : "fail";

    private static string MeaningLabel(MeaningVerdict verdict) =>
        verdict == MeaningVerdict.NotEvaluated ? "not checked" : verdict.ToString().ToLowerInvariant();

    private static bool HasMeaningIssues(string[]? issues) => issues is not null && issues.Length > 0;

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director qa --input <segments.json> --output <report.json> [options]");
        Console.WriteLine();
        Console.WriteLine("Run translate + QA timing+meaning check over a segments file. Each segment may carry");
        Console.WriteLine("an optional duration_seconds (dubbing window). When absent, the window is");
        Console.WriteLine("estimated from the source line's spoken duration at the configured CPS.");
        Console.WriteLine("A deterministic duration estimator (chars / cps) checks timing; a meaning-fidelity");
        Console.WriteLine("model check (subject/action/negation/modality/numbers/entities/objects; idiom allowed)");
        Console.WriteLine("checks meaning when timing passes. Failures bounce back to the model for revision");
        Console.WriteLine("up to --max-attempts (meaning Error fails without revision). Never silently accepted.");
        Console.WriteLine();
        Console.WriteLine("  --input <path>        Input JSON: array of {id, text, duration_seconds?} (required).");
        Console.WriteLine("  --output <path>       Output report JSON path (required).");
        Console.WriteLine("  --from <code>         Source language code. Default: en");
        Console.WriteLine("  --to <code>           Target language code. Default: es");
        Console.WriteLine("  --max-attempts <n>    Max revision attempts after initial. Default: 3");
        Console.WriteLine("  --tolerance <frac>    Timing tolerance fraction. Default: 0.10 (10%)");
        Console.WriteLine("  --cps <n>             Target language characters per second. Default: 15");
        Console.WriteLine("  -h, --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
        Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1");
        Console.WriteLine("  META_REASONING_EFFORT                   low|medium|high. Default: low (short translation)");
        Console.WriteLine("  QA_MAX_ATTEMPTS, QA_TOLERANCE, QA_CPS_<LANG>  Optional overrides for qa options.");
        Console.WriteLine();
        Console.WriteLine("Report JSON: per segment the window, first-attempt estimate+verdict, each");
        Console.WriteLine("revision attempt, final verdict, and token usage. Totals: first-pass rate,");
        Console.WriteLine("final pass rate, total revisions, total tokens.");
    }
}
