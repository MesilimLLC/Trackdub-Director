using Trackdub.Director.Core;

internal static class IngestCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? input = null;
        string? output = null;
        string? resultOutput = null;
        string? rawOutput = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    input = args[i + 1];
                    i++;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[i + 1];
                    i++;
                    break;
                case "--result" when i + 1 < args.Length:
                    resultOutput = args[i + 1];
                    i++;
                    break;
                case "--raw" when i + 1 < args.Length:
                    rawOutput = args[i + 1];
                    i++;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}' for 'ingest'.");
                    PrintHelp();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("error: --input <audio.wav> is required.");
            PrintHelp();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("error: --output <segments.json> is required.");
            PrintHelp();
            return 1;
        }

        string resolvedInput = Path.GetFullPath(input);
        if (!File.Exists(resolvedInput))
        {
            Console.Error.WriteLine($"error: input file not found: {resolvedInput}");
            return 1;
        }

        string? pathClash = ValidateOutputPaths(resolvedInput, output, resultOutput, rawOutput);
        if (pathClash is not null)
        {
            Console.Error.WriteLine($"error: {pathClash}");
            return 1;
        }

        try
        {
            var service = string.IsNullOrWhiteSpace(rawOutput) ? new TranscribeService() : new TranscribeService(Path.GetFullPath(rawOutput));
            Console.WriteLine($"Transcribing {resolvedInput} (POST /v1/asr/transcribe, DIARIZATION)...");
            TranscribeResult result = await service.TranscribeFileAsync(resolvedInput).ConfigureAwait(false);
            string resolvedOutput = Path.GetFullPath(output);
            await service.WriteSegmentsAsync(resolvedOutput, result).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(resultOutput))
            {
                await service.WriteResultAsync(Path.GetFullPath(resultOutput), result).ConfigureAwait(false);
            }

            Console.WriteLine($"Wrote {result.Segments.Length} segment(s) to {resolvedOutput}");
            Console.WriteLine($"Audio: {result.AudioDurationMs} ms | session: {result.SessionId ?? "(none)"} | usage: not reported by the endpoint (null, never zero)");
            foreach (TranscribeSegment segment in result.Segments)
            {
                string duration = segment.DurationSeconds?.ToString("0.0") ?? "?";
                Console.WriteLine($"  {segment.Id} [{segment.Speaker ?? "?"}] {duration}s  {segment.Text}");
            }

            return 0;
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
}
