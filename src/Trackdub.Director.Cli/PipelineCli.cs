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
        string? audio = null;
        string? input = null;
        string? sceneInput = null;
        string? output = null;
        string from = "en";
        string to = "es";
        bool scene = true;
        bool audition = false;
        int maxAttempts = 3;
        double tolerance = 0.10;
        double cps = DurationEstimator.DefaultCharactersPerSecond;
        bool hasMaxAttempts = false;
        bool hasTolerance = false;
        bool hasCps = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--audio" when i + 1 < args.Length:
                    audio = args[i + 1];
                    i++;
                    break;
                case "--input" when i + 1 < args.Length:
                    input = args[i + 1];
                    i++;
                    break;
                case "--scene-input" when i + 1 < args.Length:
                    sceneInput = args[i + 1];
                    i++;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[i + 1];
                    i++;
                    break;
                case "--from" when i + 1 < args.Length:
                    from = args[i + 1];
                    i++;
                    break;
                case "--to" when i + 1 < args.Length:
                    to = args[i + 1];
                    i++;
                    break;
                case "--no-scene":
                    scene = false;
                    break;
                case "--audition":
                    audition = true;
                    break;
                case "--max-attempts" when i + 1 < args.Length:
                    if (!int.TryParse(args[i + 1], out maxAttempts) || maxAttempts < 0)
                    {
                        Console.Error.WriteLine("error: --max-attempts must be a non-negative integer.");
                        return 1;
                    }

                    hasMaxAttempts = true;
                    i++;
                    break;
                case "--tolerance" when i + 1 < args.Length:
                    if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tolerance) || tolerance < 0)
                    {
                        Console.Error.WriteLine("error: --tolerance must be a non-negative number (fraction, e.g. 0.10 for 10%).");
                        return 1;
                    }

                    hasTolerance = true;
                    i++;
                    break;
                case "--cps" when i + 1 < args.Length:
                    if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cps) || cps <= 0)
                    {
                        Console.Error.WriteLine("error: --cps must be a positive number (characters per second).");
                        return 1;
                    }

                    hasCps = true;
                    i++;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}' for 'pipeline'.");
                    PrintHelp();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(audio) && string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("error: --audio <file.wav> or --input <segments.json> is required.");
            PrintHelp();
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(audio) && !string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("error: pass either --audio or --input, not both.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("error: --output <pipeline-report.json> is required.");
            PrintHelp();
            return 1;
        }

        string? pathClash = ValidateOutputPath(output, audio, input, sceneInput);
        if (pathClash is not null)
        {
            Console.Error.WriteLine($"error: {pathClash}");
            return 1;
        }

        try
        {
            PipelineRequest? request = await BuildRequestAsync(audio, input, sceneInput, scene, audition, from, to).ConfigureAwait(false);
            if (request is null)
            {
                return 1;
            }

            QaOptions envOptions = QaOptions.FromEnvironment();
            var cpsOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in envOptions.CharactersPerSecondByLanguage)
            {
                cpsOverrides[kv.Key] = kv.Value;
            }

            if (hasCps)
            {
                cpsOverrides[to.Trim().ToLowerInvariant()] = cps;
            }

            var options = new QaOptions
            {
                MaxAttempts = hasMaxAttempts ? maxAttempts : envOptions.MaxAttempts,
                ToleranceFraction = hasTolerance ? tolerance : envOptions.ToleranceFraction,
                CharactersPerSecondByLanguage = cpsOverrides,
                ReasoningEffort = envOptions.ReasoningEffort,
            };

            using var client = new MetaClient();
            var service = new PipelineService(client, new MuseTranscribeClient(), new ElevenLabsTtsProvider(), options);
            Console.WriteLine($"Pipeline: {(audio is not null ? $"audio {Path.GetFullPath(audio)}" : $"{request.Segments!.Count} segment(s)")} {from}->{to} | scene {(scene ? "on" : "off")} | audition {(audition ? "on" : "off")}");
            PipelineReport report = await service.RunAsync(request).ConfigureAwait(false);

            if (report.Audition is not null)
            {
                await TryWriteAuditionOutputsAsync(report.Audition).ConfigureAwait(false);
            }

            string resolvedOutput = Path.GetFullPath(output);
            string? dir = Path.GetDirectoryName(resolvedOutput);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(resolvedOutput, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);

            foreach (PipelineStageReport stage in report.Stages)
            {
                string detail = stage.Error is not null ? $" | error: {stage.Error}" : stage.Note is not null ? $" | {stage.Note}" : string.Empty;
                Console.WriteLine($"  {stage.Stage}: {stage.Status}{detail}");
            }

            if (report.Qa is not null)
            {
                QaCli.PrintSummary(report.Qa);
            }

            Console.WriteLine($"Report written to {resolvedOutput}");
            return report.Stages.Any(s => s.Status == PipelineStageStatus.Failed) ? 1 : 0;
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
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<PipelineRequest?> BuildRequestAsync(string? audio, string? input, string? sceneInput, bool scene, bool audition, string from, string to)
    {
        byte[]? audioBytes = null;
        string? audioFileName = null;
        TranscribeSegment[]? segments = null;

        if (!string.IsNullOrWhiteSpace(audio))
        {
            string resolvedAudio = Path.GetFullPath(audio);
            if (!File.Exists(resolvedAudio))
            {
                Console.Error.WriteLine($"error: audio file not found: {resolvedAudio}");
                return null;
            }

            audioBytes = await File.ReadAllBytesAsync(resolvedAudio).ConfigureAwait(false);
            audioFileName = Path.GetFileName(resolvedAudio);
        }
        else
        {
            string resolvedInput = Path.GetFullPath(input!);
            if (!File.Exists(resolvedInput))
            {
                Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
                return null;
            }

            string json = await File.ReadAllTextAsync(resolvedInput).ConfigureAwait(false);
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
        }

        SceneSegmentInput[]? sceneSegments = null;
        if (!string.IsNullOrWhiteSpace(sceneInput))
        {
            string resolvedScene = Path.GetFullPath(sceneInput);
            if (!File.Exists(resolvedScene))
            {
                Console.Error.WriteLine($"error: scene input file not found: {resolvedScene}");
                return null;
            }

            string json = await File.ReadAllTextAsync(resolvedScene).ConfigureAwait(false);
            try
            {
                sceneSegments = JsonSerializer.Deserialize<SceneSegmentInput[]>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"error: invalid scene JSON in '{resolvedScene}': {ex.Message}");
                return null;
            }
        }

        return new PipelineRequest(audioBytes, segments, sceneSegments, scene, audition, from, to, audioFileName);
    }

    private static string? ValidateOutputPath(string output, string? audio, string? input, string? sceneInput)
    {
        string resolvedOutput;
        try
        {
            resolvedOutput = Path.GetFullPath(output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "--output must be a valid file path.";
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach ((string label, string? path) in new (string, string?)[]
        {
            ("--audio", audio),
            ("--input", input),
            ("--scene-input", sceneInput),
        })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string resolved;
            try
            {
                resolved = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
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

    /// <summary>
    /// Persists winning takes when AUDITION_OUTPUT_DIR is set (same contract as the
    /// server). AudioBytes are JsonIgnore, so the report JSON alone would drop them.
    /// </summary>
    private static async Task TryWriteAuditionOutputsAsync(AuditionReport report)
    {
        string? outputDir = Environment.GetEnvironmentVariable("AUDITION_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        string auditionDir = Path.Combine(outputDir, "media", "audition");
        Directory.CreateDirectory(auditionDir);
        foreach (AuditionSegmentReport segment in report.Segments)
        {
            AuditionTakeReport? winner = segment.Takes.FirstOrDefault(t => t.IsWinner);
            if (winner?.AudioBytes is null || segment.WinnerFileName is null)
            {
                continue;
            }

            string root = Path.GetFullPath(auditionDir) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(auditionDir, segment.WinnerFileName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Winning take file name resolves outside the audition directory: {segment.WinnerFileName}");
            }

            await File.WriteAllBytesAsync(target, winner.AudioBytes).ConfigureAwait(false);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "audition-manifest.json"),
            JsonSerializer.Serialize(report, options)).ConfigureAwait(false);
        Console.WriteLine($"Audition winners written under {auditionDir}");
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
        Console.WriteLine("  AUDITION_OUTPUT_DIR                     When set with --audition, write winning WAVs + manifest.");
        Console.WriteLine("  META_TRANSCRIBE_MODEL                   Default: muse-voice-transcribe-1.0");
        Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
    }
}
