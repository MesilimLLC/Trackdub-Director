using Trackdub.Director.Core;

internal static class IngestCli
{
    private static readonly string[] ValueOptions = ["--input", "--output", "--result", "--raw"];

    public static async Task<int> RunAsync(string[] args)
    {
        IngestArgs? parsed = ParseArgs(args, out int exitCode);
        if (parsed is null)
        {
            return exitCode;
        }

        if (!HasRequiredOptions(parsed))
        {
            return 1;
        }

        string resolvedInput = Path.GetFullPath(parsed.Input!);
        if (!File.Exists(resolvedInput))
        {
            Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
            return 1;
        }

        string? pathClash = ValidateOutputPaths(resolvedInput, parsed.Output!, parsed.ResultOutput, parsed.RawOutput);
        if (pathClash is not null)
        {
            Console.Error.WriteLine($"error: {pathClash}");
            return 1;
        }

        try
        {
            return await TranscribeAsync(parsed, resolvedInput).ConfigureAwait(false);
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

    // Returns null when the command should stop (help shown or a bad option); exitCode carries the result.
    private static IngestArgs? ParseArgs(string[] args, out int exitCode)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        exitCode = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help")
            {
                PrintHelp();
                return null;
            }

            if (!ValueOptions.Contains(args[i]) || i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Unknown option '{args[i]}' for 'ingest'.");
                PrintHelp();
                exitCode = 1;
                return null;
            }

            values[args[i]] = args[i + 1];
            i++;
        }

        return new IngestArgs(
            Input: Get(values, "--input"),
            Output: Get(values, "--output"),
            ResultOutput: Get(values, "--result"),
            RawOutput: Get(values, "--raw"));
    }

    private static string? Get(Dictionary<string, string> values, string option) =>
        values.TryGetValue(option, out string? value) ? value : null;

    private static bool HasRequiredOptions(IngestArgs parsed)
    {
        if (string.IsNullOrWhiteSpace(parsed.Input))
        {
            Console.Error.WriteLine("error: --input <audio.wav> is required.");
            PrintHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Output))
        {
            Console.Error.WriteLine("error: --output <segments.json> is required.");
            PrintHelp();
            return false;
        }

        return true;
    }

    private static async Task<int> TranscribeAsync(IngestArgs parsed, string resolvedInput)
    {
        var service = string.IsNullOrWhiteSpace(parsed.RawOutput) ? new TranscribeService() : new TranscribeService(Path.GetFullPath(parsed.RawOutput));
        Console.WriteLine($"Transcribing {resolvedInput} (POST /v1/asr/transcribe, DIARIZATION)...");
        TranscribeResult result = await service.TranscribeFileAsync(resolvedInput).ConfigureAwait(false);
        string resolvedOutput = Path.GetFullPath(parsed.Output!);
        await service.WriteSegmentsAsync(resolvedOutput, result).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(parsed.ResultOutput))
        {
            await service.WriteResultAsync(Path.GetFullPath(parsed.ResultOutput), result).ConfigureAwait(false);
        }

        PrintSummary(result, resolvedOutput);
        return 0;
    }

    private static void PrintSummary(TranscribeResult result, string resolvedOutput)
    {
        Console.WriteLine($"Wrote {result.Segments.Length} segment(s) to {resolvedOutput}");
        Console.WriteLine($"Audio: {result.AudioDurationMs} ms | session: {result.SessionId ?? "(none)"} | usage: not reported by the endpoint (null, never zero)");
        foreach (TranscribeSegment segment in result.Segments)
        {
            string duration = segment.DurationSeconds?.ToString("0.0") ?? "?";
            Console.WriteLine($"  {segment.Id} [{segment.Speaker ?? "?"}] {duration}s  {segment.Text}");
        }
    }

    private static string? ValidateOutputPaths(string resolvedInput, string output, string? resultOutput, string? rawOutput)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach ((string label, string? path) in new (string, string?)[]
        {
            ("--output", output),
            ("--result", resultOutput),
            ("--raw", rawOutput),
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

            if (string.Equals(resolved, resolvedInput, comparison))
            {
                return $"{label} must not be the same path as --input.";
            }
        }

        return null;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director ingest --input <audio.wav> --output <segments.json> [--result <transcribe-result.json>] [--raw <raw-response.json>]");
        Console.WriteLine();
        Console.WriteLine("Transcribe a WAV file through the Meta ASR endpoint (POST /v1/asr/transcribe,");
        Console.WriteLine("mode DIARIZATION, model muse-voice-transcribe-1.0) and write the segments in the");
        Console.WriteLine("app's input shape: array of {id, text, duration_seconds, speaker}. The speaker");
        Console.WriteLine("label is the diarization label recorded verbatim, never mapped to a name.");
        Console.WriteLine("The endpoint reports no token usage; usage is recorded as null, never zero.");
        Console.WriteLine();
        Console.WriteLine("  --input <path>     Input WAV file (required).");
        Console.WriteLine("  --output <path>    Segments JSON to write (required).");
        Console.WriteLine("  --result <path>    Optional: full transcribe result JSON (turns, stamps, session).");
        Console.WriteLine("  --raw <path>       Optional: preserve the raw response body (written before parsing).");
        Console.WriteLine("  -h, --help         Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
        Console.WriteLine("  META_TRANSCRIBE_MODEL                   Default: muse-voice-transcribe-1.0");
        Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
    }

    private sealed record IngestArgs(string? Input, string? Output, string? ResultOutput, string? RawOutput);
}
