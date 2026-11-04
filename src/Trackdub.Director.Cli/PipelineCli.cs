using System.Globalization;
using System.Text.Json;
using Trackdub.Director.Core;

internal static class PipelineCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new PipelineArgumentParser(args);
        if (!parser.TryParse())
        {
            return parser.ExitCode;
        }

        PipelineOptions options = parser.Options;
        (string? error, bool showHelp) = ValidateOptions(options);
        if (error is not null)
        {
            Console.Error.WriteLine($"error: {error}");
            if (showHelp)
            {
                PrintHelp();
            }

            return 1;
        }

        try
        {
            return await ExecutePipelineAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecutePipelineAsync(PipelineOptions options)
    {
        // Pre-flight auth: a missing hosted key is a configuration error, not a QA stage that
        // reports every segment as failed with a generic "translation generation failed upstream".
        _ = MetaAuth.GetApiKey();

        PipelineRequest? request = await BuildRequestAsync(options).ConfigureAwait(false);
        if (request is null)
        {
            return 1;
        }

        request = ApplyRequestOptions(request, options);

        QaOptions qaOptions = BuildQaOptions(options);
        using var client = new MetaClient();
        var service = new PipelineService(client, new MuseTranscribeClient(), new ElevenLabsTtsProvider(), qaOptions);
        Console.WriteLine($"Pipeline: {DescribeRequest(request, options)}");
        PipelineReport report = await service.RunAsync(request).ConfigureAwait(false);

        if (report.Audition is not null)
        {
            await TryWriteAuditionOutputsAsync(report.Audition).ConfigureAwait(false);
        }

        await WriteAndPrintReportAsync(report, options).ConfigureAwait(false);
        return report.Stages.Any(s => s.Status == PipelineStageStatus.Failed) ? 1 : 0;
    }

    private static PipelineRequest ApplyRequestOptions(PipelineRequest request, PipelineOptions options)
    {
        return request with
        {
            SpeedRetake = options.SpeedRetake ? new SpeedRetakeOptions(Enabled: true, Cap: options.SpeedRetakeCap) : null,
            EmotionTags = options.EmotionTags ? null : EmotionTagOptions.Disabled,
        };
    }

    private static string DescribeRequest(PipelineRequest request, PipelineOptions options)
    {
        string source = options.Audio is not null ? $"audio {Path.GetFullPath(options.Audio)}" : $"{request.Segments!.Count} segment(s)";
        string scene = options.Scene ? "on" : "off";
        string audition = options.Audition ? "on" : "off";
        return $"{source} {options.From}->{options.To} | scene {scene} | audition {audition}";
    }

    private static QaOptions BuildQaOptions(PipelineOptions options)
    {
        QaOptions envOptions = QaOptions.FromEnvironment();
        return new QaOptions
        {
            MaxAttempts = options.HasMaxAttempts ? options.MaxAttempts : envOptions.MaxAttempts,
            ToleranceFraction = options.HasTolerance ? options.Tolerance : envOptions.ToleranceFraction,
            CharactersPerSecondByLanguage = BuildCpsOverrides(options, envOptions),
            ReasoningEffort = envOptions.ReasoningEffort,
        };
    }

    private static Dictionary<string, double> BuildCpsOverrides(PipelineOptions options, QaOptions envOptions)
    {
        var cpsOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in envOptions.CharactersPerSecondByLanguage)
        {
            cpsOverrides[kv.Key] = kv.Value;
        }

        if (options.HasCps)
        {
            cpsOverrides[options.To.Trim().ToLowerInvariant()] = options.Cps;
        }

        return cpsOverrides;
    }

    private static async Task WriteAndPrintReportAsync(PipelineReport report, PipelineOptions options)
    {
        string resolvedOutput = Path.GetFullPath(options.Output!);
        string? dir = Path.GetDirectoryName(resolvedOutput);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(resolvedOutput, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);

        foreach (PipelineStageReport stage in report.Stages)
        {
            PrintStage(stage);
        }

        if (report.Qa is not null)
        {
            QaCli.PrintSummary(report.Qa);
        }

        Console.WriteLine($"Report written to {resolvedOutput}");
    }

    private static void PrintStage(PipelineStageReport stage)
    {
        string detail = stage.Error is not null ? $" | error: {stage.Error}" : stage.Note is not null ? $" | {stage.Note}" : string.Empty;
        Console.WriteLine($"  {stage.Stage}: {stage.Status}{detail}");
    }

    private static (string? Error, bool ShowHelp) ValidateOptions(PipelineOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Audio) && string.IsNullOrWhiteSpace(options.Input))
        {
            return ("--audio <file.wav> or --input <segments.json> is required.", true);
        }

        if (!string.IsNullOrWhiteSpace(options.Audio) && !string.IsNullOrWhiteSpace(options.Input))
        {
            return ("pass either --audio or --input, not both.", false);
        }

        if (string.IsNullOrWhiteSpace(options.Output))
        {
            return ("--output <pipeline-report.json> is required.", true);
        }

        return (ValidateOutputPath(options), false);
    }

    private static async Task<PipelineRequest?> BuildRequestAsync(PipelineOptions options)
    {
        InputData? input = await LoadInputAsync(options).ConfigureAwait(false);
        if (input is null)
        {
            return null;
        }

        SceneSegmentInput[]? sceneSegments = null;
        if (!string.IsNullOrWhiteSpace(options.SceneInput))
        {
            (SceneSegmentInput[]? segments, bool ok) = await LoadSceneAsync(options).ConfigureAwait(false);
            if (!ok)
            {
                return null;
            }

            sceneSegments = segments;
        }

        return new PipelineRequest(input.AudioBytes, input.Segments, sceneSegments, options.Scene, options.Audition, options.From, options.To, input.AudioFileName);
    }

    private static async Task<InputData?> LoadInputAsync(PipelineOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Audio))
        {
            string resolvedAudio = Path.GetFullPath(options.Audio);
            if (!File.Exists(resolvedAudio))
            {
                Console.Error.WriteLine($"error: audio file not found: {resolvedAudio}");
                return null;
            }

            byte[] audioBytes = await File.ReadAllBytesAsync(resolvedAudio).ConfigureAwait(false);
            return new InputData
            {
                AudioBytes = audioBytes,
                AudioFileName = Path.GetFileName(resolvedAudio),
            };
        }

        return await LoadSegmentsAsync(options).ConfigureAwait(false);
    }

    private static async Task<InputData?> LoadSegmentsAsync(PipelineOptions options)
    {
        string resolvedInput = Path.GetFullPath(options.Input!);
        if (!File.Exists(resolvedInput))
        {
            Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
            return null;
        }

        string json = await File.ReadAllTextAsync(resolvedInput).ConfigureAwait(false);
        TranscribeSegment[]? segments;
        try
        {
            segments = JsonSerializer.Deserialize<TranscribeSegment[]?>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"error: invalid segments JSON in '{resolvedInput}': {ex.Message}");
            return null;
        }

        if (segments is null || segments.Length == 0)
        {
            Console.Error.WriteLine($"error: empty or null JSON in '{resolvedInput}'. Expected an array of {{id, text, duration_seconds}}.");
            return null;
        }

        return new InputData { Segments = segments };
    }

    private static async Task<(SceneSegmentInput[]? Segments, bool Ok)> LoadSceneAsync(PipelineOptions options)
    {
        string resolvedScene = Path.GetFullPath(options.SceneInput!);
        if (!File.Exists(resolvedScene))
        {
            Console.Error.WriteLine($"error: scene input file not found: {resolvedScene}");
            return (null, false);
        }

        string json = await File.ReadAllTextAsync(resolvedScene).ConfigureAwait(false);
        try
        {
            return (JsonSerializer.Deserialize<SceneSegmentInput[]>(json, JsonOptions), true);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"error: invalid scene JSON in '{resolvedScene}': {ex.Message}");
            return (null, false);
        }
    }

    private static string? ValidateOutputPath(PipelineOptions options)
    {
        if (!TryResolvePath(options.Output!, out string resolvedOutput))
        {
            return "--output must be a valid file path.";
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach ((string label, string? path) in new (string, string?)[]
        {
            ("--audio", options.Audio),
            ("--input", options.Input),
            ("--scene-input", options.SceneInput),
        })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!TryResolvePath(path, out string resolved))
            {
                return $"{label} must be a valid file path.";
            }

            if (string.Equals(resolvedOutput, resolved, comparison))
            {
                return $"--output must not be the same path as {label}.";
            }
        }

        return null;
    }

    private static bool TryResolvePath(string path, out string resolved)
    {
        try
        {
            resolved = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Persists every synthesized take and the manifest when AUDITION_OUTPUT_DIR is set (same
    /// contract as the server). AudioBytes are JsonIgnore, so the report JSON alone would drop them.
    /// </summary>
    private static async Task TryWriteAuditionOutputsAsync(AuditionReport report)
    {
        string? outputDir = Environment.GetEnvironmentVariable("AUDITION_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        await AuditionOutputWriter.WriteAsync(report, outputDir).ConfigureAwait(false);
        Console.WriteLine($"Audition takes written under {Path.Combine(outputDir, "media", "audition")}");
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director pipeline (--audio <file.wav> | --input <segments.json>) --output <report.json> [options]");
        Console.WriteLine();
        Console.WriteLine("Run the lanes in order by calling the existing services: transcribe (only when");
        Console.WriteLine("--audio is supplied), then translation + QA, then scene for segments that have");
        Console.WriteLine("frames (--scene-input), then audition only when --audition is passed. No lane");
        Console.WriteLine("logic is duplicated; each lane's report keeps its existing shape and verdicts.");
        Console.WriteLine("Per-stage status is Ran / SkippedByFlag / Failed (a flag-skipped stage is never");
        Console.WriteLine("a pass or a failure); a failed stage stops the run and later stages never run.");
        Console.WriteLine("Director adaptation is not a pipeline stage (per-line opt-in by design).");
        Console.WriteLine();
        Console.WriteLine("  --audio <path>        WAV audio to transcribe as stage 1.");
        Console.WriteLine("  --input <path>        Segments JSON: array of {id, text, duration_seconds, speaker?}.");
        Console.WriteLine("  --scene-input <path>  Scene inputs JSON (frames/concepts per segment id).");
        Console.WriteLine("  --output <path>       Pipeline report JSON to write (required).");
        Console.WriteLine("  --no-scene            Skip the scene stage (recorded as SkippedByFlag).");
        Console.WriteLine("  --audition            Run the audition stage (off by default; needs ELEVENLABS_API_KEY).");
        Console.WriteLine("  --speed-retake [--speed-retake-cap <1.0-1.2>]  Enable the computed-speed retake in audition (default cap 1.10).");
        Console.WriteLine("  --no-emotion-tags  Opt out of emotion/pacing audio tags in audition (on by default on tag-honoring models).");
        Console.WriteLine("  --from <code>         Source language code. Default: en");
        Console.WriteLine("  --to <code>           Target language code. Default: es");
        Console.WriteLine("  --max-attempts <n>    QA max revision attempts after initial. Default: 3");
        Console.WriteLine("  --tolerance <frac>    QA timing tolerance fraction. Default: 0.10 (10%)");
        Console.WriteLine("  --cps <n>             Target language characters per second. Default: 15");
        Console.WriteLine("  -h, --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
        Console.WriteLine("  ELEVENLABS_API_KEY                      Required only for --audition.");
        Console.WriteLine("  AUDITION_OUTPUT_DIR                     When set with --audition, write every synthesized take (winners and losers) + manifest.");
        Console.WriteLine("  META_TRANSCRIBE_MODEL                   Default: muse-voice-transcribe-1.0");
        Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
    }

    private enum ArgumentResult
    {
        NotHandled,
        Handled,
        Failed,
    }

    private sealed class PipelineOptions
    {
        public string? Audio;
        public string? Input;
        public string? SceneInput;
        public string? Output;
        public string From = "en";
        public string To = "es";
        public bool Scene = true;
        public bool Audition;
        public bool SpeedRetake;
        public double SpeedRetakeCap = SpeedRetakeMath.DefaultCap;
        public bool EmotionTags = true;
        public int MaxAttempts = 3;
        public double Tolerance = 0.10;
        public double Cps = DurationEstimator.DefaultCharactersPerSecond;
        public bool HasMaxAttempts;
        public bool HasTolerance;
        public bool HasCps;
    }

    private sealed class InputData
    {
        public byte[]? AudioBytes;
        public string? AudioFileName;
        public TranscribeSegment[]? Segments;
    }

    private sealed class PipelineArgumentParser
    {
        private readonly string[] _args;
        private int _index;

        public PipelineArgumentParser(string[] args)
        {
            _args = args;
        }

        public PipelineOptions Options = new();

        public int ExitCode;

        private bool HasValue => _index + 1 < _args.Length;

        public bool TryParse()
        {
            for (_index = 0; _index < _args.Length; _index++)
            {
                if (IsHelp())
                {
                    PrintHelp();
                    return false;
                }

                ArgumentResult result = TryApplyArgument();
                if (result == ArgumentResult.Failed)
                {
                    return false;
                }

                if (result == ArgumentResult.Handled)
                {
                    continue;
                }

                ReportUnknownOption();
                return false;
            }

            return true;
        }

        private bool IsHelp()
        {
            return _args[_index] is "-h" or "--help";
        }

        private void ReportUnknownOption()
        {
            Console.Error.WriteLine($"Unknown option '{_args[_index]}' for 'pipeline'.");
            PrintHelp();
            ExitCode = 1;
        }

        private ArgumentResult TryApplyArgument()
        {
            ArgumentResult result = TryApplyPathArgument();
            if (result != ArgumentResult.NotHandled)
            {
                return result;
            }

            result = TryApplyLanguageArgument();
            if (result != ArgumentResult.NotHandled)
            {
                return result;
            }

            result = TryApplyFlagArgument();
            if (result != ArgumentResult.NotHandled)
            {
                return result;
            }

            return TryApplyNumericArgument();
        }

        private ArgumentResult TryApplyPathArgument()
        {
            if (!HasValue)
            {
                return ArgumentResult.NotHandled;
            }

            switch (_args[_index])
            {
                case "--audio":
                    Options.Audio = NextValue();
                    break;
                case "--input":
                    Options.Input = NextValue();
                    break;
                case "--scene-input":
                    Options.SceneInput = NextValue();
                    break;
                case "--output":
                    Options.Output = NextValue();
                    break;
                default:
                    return ArgumentResult.NotHandled;
            }

            _index++;
            return ArgumentResult.Handled;
        }

        private ArgumentResult TryApplyLanguageArgument()
        {
            if (!HasValue)
            {
                return ArgumentResult.NotHandled;
            }

            switch (_args[_index])
            {
                case "--from":
                    Options.From = NextValue();
                    break;
                case "--to":
                    Options.To = NextValue();
                    break;
                default:
                    return ArgumentResult.NotHandled;
            }

            _index++;
            return ArgumentResult.Handled;
        }

        private ArgumentResult TryApplyFlagArgument()
        {
            switch (_args[_index])
            {
                case "--no-scene":
                    Options.Scene = false;
                    return ArgumentResult.Handled;
                case "--audition":
                    Options.Audition = true;
                    return ArgumentResult.Handled;
                case "--speed-retake":
                    Options.SpeedRetake = true;
                    return ArgumentResult.Handled;
                case "--no-emotion-tags":
                    Options.EmotionTags = false;
                    return ArgumentResult.Handled;
                default:
                    return ArgumentResult.NotHandled;
            }
        }

        private ArgumentResult TryApplyNumericArgument()
        {
            if (!HasValue)
            {
                return ArgumentResult.NotHandled;
            }

            string option = _args[_index];
            if (option == "--speed-retake-cap")
            {
                return ApplySpeedRetakeCap();
            }

            if (option == "--max-attempts")
            {
                return ApplyMaxAttempts();
            }

            if (option == "--tolerance")
            {
                return ApplyTolerance();
            }

            if (option == "--cps")
            {
                return ApplyCps();
            }

            return ArgumentResult.NotHandled;
        }

        private ArgumentResult ApplySpeedRetakeCap()
        {
            if (TryParseSpeedRetakeCap() is not double cap)
            {
                Console.Error.WriteLine($"error: --speed-retake-cap must be between 1.0 and {SpeedRetakeMath.HardCeiling}.");
                ExitCode = 1;
                return ArgumentResult.Failed;
            }

            Options.SpeedRetakeCap = cap;
            Options.SpeedRetake = true;
            _index++;
            return ArgumentResult.Handled;
        }

        private double? TryParseSpeedRetakeCap()
        {
            if (!double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap))
            {
                return null;
            }

            return cap >= 1.0 && cap <= SpeedRetakeMath.HardCeiling ? cap : null;
        }

        private ArgumentResult ApplyMaxAttempts()
        {
            if (!int.TryParse(NextValue(), out int maxAttempts) || maxAttempts < 0)
            {
                Console.Error.WriteLine("error: --max-attempts must be a non-negative integer.");
                ExitCode = 1;
                return ArgumentResult.Failed;
            }

            Options.MaxAttempts = maxAttempts;
            Options.HasMaxAttempts = true;
            _index++;
            return ArgumentResult.Handled;
        }

        private ArgumentResult ApplyTolerance()
        {
            if (!double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tolerance) || tolerance < 0)
            {
                Console.Error.WriteLine("error: --tolerance must be a non-negative number (fraction, e.g. 0.10 for 10%).");
                ExitCode = 1;
                return ArgumentResult.Failed;
            }

            Options.Tolerance = tolerance;
            Options.HasTolerance = true;
            _index++;
            return ArgumentResult.Handled;
        }

        private ArgumentResult ApplyCps()
        {
            if (!double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cps) || cps <= 0)
            {
                Console.Error.WriteLine("error: --cps must be a positive number (characters per second).");
                ExitCode = 1;
                return ArgumentResult.Failed;
            }

            Options.Cps = cps;
            Options.HasCps = true;
            _index++;
            return ArgumentResult.Handled;
        }

        private string NextValue()
        {
            return _args[_index + 1];
        }
    }
}
