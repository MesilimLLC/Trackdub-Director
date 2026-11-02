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
        var command = new CommandArgs(args);
        if (command.Count == 0 || command.WantsHelp())
        {
            PrintHelp();
            return command.Count == 0 ? 1 : 0;
        }

        Subcommand sub = command.Subcommand();
        CommandArgs rest = command.Rest();
        if (rest.WantsHelp())
        {
            // Subcommand help must never trigger a metered Spark Open call.
            PrintHelp();
            return 0;
        }

        return await DispatchAsync(sub, rest).ConfigureAwait(false);
    }

    private static Task<int> DispatchAsync(Subcommand sub, CommandArgs rest)
    {
        return sub.Value switch
        {
            "judge" => RunJudgeAsync(rest),
            "production" => RunProductionAsync(rest),
            "translation" => RunTranslationAsync(rest),
            "smoke" => RunSmokeAsync(rest),
            _ => Task.FromResult(UnknownSub(sub)),
        };
    }

    private static int UnknownSub(Subcommand sub)
    {
        Console.Error.WriteLine($"Unknown spark-open subcommand '{sub.Value}'. Expected 'judge', 'production', 'translation', or 'smoke'.");
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

    private static async Task<int> RunJudgeAsync(CommandArgs args)
    {
        CommonOptions? opts = ParseCommon(args, out ParseError? error);
        if (opts is null)
        {
            Console.Error.WriteLine($"error: {error?.Value}");
            return 1;
        }

        if (!opts.IsEnglishToSpanish())
        {
            // The captured judge inputs are all en->es; judging them under any other pair would diff
            // Spark Open verdicts against hosted verdicts recorded for a different target language.
            Console.Error.WriteLine("error: the captured judge inputs are all en->es; --from must be 'en' and --to must be 'es'.");
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        return await GuardAsync(async () =>
        {
            IReadOnlyList<JudgeItem> items = await JudgeItemLoader.LoadAsync(
                opts.CasesPath.Value,
                opts.PressurePath.Value,
                opts.UniformPaths.Select(p => p.Value).ToArray()).ConfigureAwait(false);
            Console.WriteLine($"Spark Open judge: {items.Count} items ({opts.From.Value}->{opts.To.Value}).");

            using var client = new SparkOpenClient();
            var runner = new SparkOpenJudgeRunner(client);
            JudgeAgreementReport report = await runner.RunAsync(items, opts.From.Value, opts.To.Value).ConfigureAwait(false);
            await runner.WriteReportAsync(opts.Output.Value, report).ConfigureAwait(false);

            JudgeAgreementSummary s = report.Summary;
            Console.WriteLine($"  compared {s.Compared}/{s.TotalItems}: agree {s.Agree}, disagree {s.Disagree}, excluded {s.Excluded}, agreement {Percent(s.AgreementRate)}, tokens {s.TotalTokens}");
            Console.WriteLine($"Report written to {opts.Output.Value}");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> RunProductionAsync(CommandArgs args)
    {
        if (!TryResolveInputOutput(args, out FilePath input, out FilePath output))
        {
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        return await RunReportLaneAsync(input, output, async (segments, reportPath) =>
        {
            using var client = new SparkOpenClient();
            var runner = new SparkOpenProductionRunner(client, QaOptions.FromEnvironment());
            SparkOpenProductionReport report = await runner.RunAsync(segments, "en", "es").ConfigureAwait(false);
            await runner.WriteReportAsync(reportPath.FullPath, report).ConfigureAwait(false);
            return $"Spark Open production: {report.QaReport.Totals.FinalPassCount}/{report.QaReport.Totals.TotalSegments} final pass, failed {report.QaReport.Totals.FailedSegments}, fully gate-verified={report.FullyGateVerified}, tokens {report.QaReport.Totals.TotalTokens}";
        }).ConfigureAwait(false);
    }

    private static async Task<int> RunTranslationAsync(CommandArgs args)
    {
        if (!TryResolveInputOutput(args, out FilePath input, out FilePath output))
        {
            return 1;
        }

        if (!EnsureConfigured())
        {
            return 2;
        }

        return await RunReportLaneAsync(input, output, async (segments, reportPath) =>
        {
            using var sparkOpen = new SparkOpenClient();
            using var hosted = new MetaClient();
            var runner = new SparkOpenTranslationRunner(sparkOpen, hosted, QaOptions.FromEnvironment());
            IReadOnlyDictionary<string, string> hostedTranslations = await LoadHostedTranslationsAsync().ConfigureAwait(false);
            SparkOpenTranslationReport report = await runner.RunAsync(
                new TranslationRunRequest(segments, "en", "es", hostedTranslations)).ConfigureAwait(false);
            await runner.WriteReportAsync(reportPath.FullPath, report).ConfigureAwait(false);

            SparkOpenTranslationSummary s = report.Summary;
            return $"Spark Open translation lane: timing {Percent(s.TimingPassRate)}, meaning {Percent(s.MeaningPassRate)}, combined {Percent(s.CombinedPassRate)} over {s.Lines} lines";
        }).ConfigureAwait(false);
    }

    private static async Task<int> RunSmokeAsync(CommandArgs args)
    {
        string text = args.ValueOf(Option.Text) ?? "Hello.";
        if (!EnsureConfigured())
        {
            return 2;
        }

        return await GuardAsync(async () =>
        {
            using var client = new SparkOpenClient();
            MetaClient meta = client.AsMetaClient();
            MetaClient.ChatCompletionResult result = await meta.ChatCompletionWithUsageAsync(
                new MetaClient.ChatRequest([new MetaClient.ChatMessage("user", $"Reply with the JSON object {{\"ok\":true}} on the final line. Context: {text}")])).ConfigureAwait(false);
            Console.WriteLine($"Spark Open smoke OK: model={result.Model}, usage total={result.Usage?.TotalTokens ?? 0}");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> RunReportLaneAsync(
        FilePath input,
        FilePath output,
        Func<IReadOnlyList<QaSegmentInput>, FilePath, Task<string>> run)
    {
        return await GuardAsync(async () =>
        {
            IReadOnlyList<QaSegmentInput> segments = await QaService.LoadSegmentsAsync(input.FullPath).ConfigureAwait(false);
            string summary = await run(segments, output).ConfigureAwait(false);
            Console.WriteLine(summary);
            Console.WriteLine($"Report written to {output.Value}");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> GuardAsync(Func<Task<int>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SparkOpenException or MetaApiException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static bool TryResolveInputOutput(CommandArgs args, out FilePath input, out FilePath output)
    {
        input = new FilePath(args.ValueOf(Option.Input) ?? string.Empty);
        output = new FilePath(args.ValueOf(Option.Output) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(input.Value) || string.IsNullOrWhiteSpace(output.Value))
        {
            Console.Error.WriteLine("error: --input <segments.json> and --output <report.json> are required.");
            return false;
        }

        if (SameFile(input, output))
        {
            Console.Error.WriteLine("error: --output must not be the same path as --input.");
            return false;
        }

        return true;
    }

    private sealed record CommonOptions(FilePath Output, JudgeInputs Inputs, LanguagePair Languages)
    {
        public FilePath CasesPath => Inputs.CasesPath;

        public FilePath PressurePath => Inputs.PressurePath;

        public IReadOnlyList<FilePath> UniformPaths => Inputs.UniformPaths;

        public LanguageCode From => Languages.From;

        public LanguageCode To => Languages.To;

        public bool IsEnglishToSpanish()
        {
            if (!string.Equals(From.Value, "en", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(To.Value, "es", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CommonOptions? ParseCommon(CommandArgs args, out ParseError? error)
    {
        string? unknown = args.FirstUnknownOption();
        if (unknown is not null)
        {
            error = new ParseError($"Unknown option '{unknown}' for 'spark-open judge'.");
            return null;
        }

        string? outputValue = args.ValueOf(Option.Output);
        if (string.IsNullOrWhiteSpace(outputValue))
        {
            error = new ParseError("--output <report.json> is required.");
            return null;
        }

        CommonOptions options = BuildOptions(args, new FilePath(outputValue));

        // The report is written over --output after the calls; it must never clobber an input file.
        if (options.Output.NamesAnyInput(options.CasesPath, options.PressurePath, options.UniformPaths))
        {
            error = new ParseError("--output must not name an input file (--cases, --pressure, or --uniform-*).");
            return null;
        }

        error = null;
        return options;
    }

    private static CommonOptions BuildOptions(CommandArgs args, FilePath output)
    {
        FilePath cases = new(args.ValueOf(Option.Cases) ?? Path.Combine("samples", "meaning-eval", "cases.json"));
        FilePath pressure = new(args.ValueOf(Option.Pressure) ?? Path.Combine("samples", "meaning-eval", "qa-report-pressure-70.json"));
        LanguageCode from = new(args.ValueOf(Option.From) ?? "en");
        LanguageCode to = new(args.ValueOf(Option.To) ?? "es");

        FilePath[] uniform =
        [
            new(args.ValueOf(Option.UniformScale) ?? Path.Combine("samples", "voa10-scale", "qa-report-scale.json")),
            new(args.ValueOf(Option.UniformVoa10) ?? Path.Combine("samples", "voa10-scale", "qa-report-voa10.json")),
        ];

        return new CommonOptions(
            new FilePath(output.FullPath),
            new JudgeInputs(
                new FilePath(cases.FullPath),
                new FilePath(pressure.FullPath),
                uniform.Select(p => new FilePath(p.FullPath)).ToArray()),
            new LanguagePair(from, to));
    }

    private static bool SameFile(FilePath input, FilePath output)
        => string.Equals(input.FullPath, output.FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the recorded hosted translations (the meaning-eval candidates) keyed by segment id, so
    /// the translation report carries each line's hosted side-by-side translation. Returns an empty
    /// map when the eval file is absent, leaving the hosted column null.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> LoadHostedTranslationsAsync(CancellationToken cancellationToken = default)
    {
        string casesPath = Path.Combine("samples", "meaning-eval", "cases.json");
        if (!File.Exists(casesPath))
        {
            return new Dictionary<string, string>();
        }

        IReadOnlyList<MeaningEvalCase> cases = await MeaningEvalService.LoadCasesAsync(casesPath, cancellationToken).ConfigureAwait(false);
        return cases
            .Where(c => !string.IsNullOrWhiteSpace(c.Candidate))
            .ToDictionary(c => c.Id, c => c.Candidate!, StringComparer.Ordinal);
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

    private readonly record struct CommandArgs(IReadOnlyList<string> Values)
    {
        public int Count => Values.Count;

        public bool WantsHelp() => Values.Any(value => value is "-h" or "--help");

        public Subcommand Subcommand() => new(Values[0].ToLowerInvariant());

        public CommandArgs Rest() => new(Values.Skip(1).ToArray());

        public string? ValueOf(Option option)
        {
            string flag = OptionFlags.ToFlag(option);
            for (int i = 0; i < Values.Count - 1; i++)
            {
                if (string.Equals(Values[i], flag, StringComparison.Ordinal))
                {
                    return Values[i + 1];
                }
            }

            return null;
        }

        public string? FirstUnknownOption()
        {
            foreach (string value in Values)
            {
                if (value.StartsWith("--", StringComparison.Ordinal) && !OptionFlags.IsKnown(value))
                {
                    return value;
                }
            }

            return null;
        }
    }

    private readonly record struct Subcommand(string Value);

    private readonly record struct FilePath(string Value)
    {
        public string FullPath => Path.GetFullPath(Value);

        public bool NamesAnyInput(FilePath cases, FilePath pressure, IReadOnlyList<FilePath> uniform)
        {
            if (SameAs(cases))
            {
                return true;
            }

            if (SameAs(pressure))
            {
                return true;
            }

            return uniform.Any(SameAs);
        }

        private bool SameAs(FilePath other)
            => string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct JudgeInputs(FilePath CasesPath, FilePath PressurePath, IReadOnlyList<FilePath> UniformPaths);

    private readonly record struct LanguagePair(LanguageCode From, LanguageCode To);

    private readonly record struct LanguageCode(string Value);

    private readonly record struct ParseError(string Value);

    private enum Option
    {
        Cases,
        Pressure,
        Output,
        From,
        To,
        UniformScale,
        UniformVoa10,
        Input,
        Text,
    }

    private static class OptionFlags
    {
        private static readonly IReadOnlyDictionary<Option, string> Flags = new Dictionary<Option, string>
        {
            [Option.Cases] = "--cases",
            [Option.Pressure] = "--pressure",
            [Option.Output] = "--output",
            [Option.From] = "--from",
            [Option.To] = "--to",
            [Option.UniformScale] = "--uniform-scale",
            [Option.UniformVoa10] = "--uniform-voa10",
            [Option.Input] = "--input",
            [Option.Text] = "--text",
        };

        public static string ToFlag(Option option) => Flags[option];

        public static bool IsKnown(string flag) => Flags.Values.Contains(flag, StringComparer.Ordinal);
    }
}
