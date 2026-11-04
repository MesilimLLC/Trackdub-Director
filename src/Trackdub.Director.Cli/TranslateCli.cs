using Trackdub.Director.Core;

internal static class TranslateCli
{
    public static async Task<int> RunAsync(CommandArgs args)
    {
        ParseResult<TranslateArguments> parsed = TryParseArgs(args);
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

        if (!TryResolvePaths(parsed.Value!, out FilePath input, out FilePath output))
        {
            return 1;
        }

        return await ExecuteAsync(parsed.Value!, input, output).ConfigureAwait(false);
    }

    private static ParseResult<TranslateArguments> TryParseArgs(CommandArgs args)
    {
        var result = new TranslateArguments();
        IReadOnlyList<string> values = args.Values;
        for (int i = 0; i < values.Count; i++)
        {
            string token = values[i];
            if (token is "-h" or "--help")
            {
                return ParseResult<TranslateArguments>.Help();
            }

            if (i + 1 >= values.Count || !TranslateOptionFlags.TryGet(token, out TranslateOption option))
            {
                return ParseResult<TranslateArguments>.Failure(new ParseError($"Unknown option '{token}' for 'translate'."));
            }

            if (!result.TryApply(option, new OptionValue(values[i + 1]), out ParseError? error))
            {
                return ParseResult<TranslateArguments>.Failure(error ?? new ParseError($"Invalid value for '{TranslateOptionFlags.ToFlag(option)}'."));
            }

            i++;
        }

        return ParseResult<TranslateArguments>.Success(result);
    }

    private static bool TryResolvePaths(TranslateArguments parsed, out FilePath input, out FilePath output)
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
            Console.Error.WriteLine("error: --output <translated.json> is required.");
            PrintHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.From) || string.IsNullOrWhiteSpace(parsed.To))
        {
            Console.Error.WriteLine("error: --from and --to must be non-empty language codes.");
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

    private static async Task<int> ExecuteAsync(TranslateArguments parsed, FilePath input, FilePath output)
    {
        try
        {
            using var client = new MetaClient();
            var service = new TranslationService(client);

            IReadOnlyList<TranslationSegmentInput> segments = await service.LoadSegmentsAsync(input.FullPath).ConfigureAwait(false);
            Console.WriteLine($"Translating {segments.Count} segment(s) from {parsed.From} to {parsed.To}...");

            TranslationBatchOutput batch = await service.TranslateBatchAsync(segments, parsed.From, parsed.To).ConfigureAwait(false);
            await service.WriteOutputAsync(output.FullPath, batch).ConfigureAwait(false);

            Console.WriteLine($"Wrote {batch.Segments.Length} translated segment(s) to {output.FullPath}");
            Console.WriteLine($"Usage total: prompt={batch.UsageTotal.PromptTokens} completion={batch.UsageTotal.CompletionTokens} total={batch.UsageTotal.TotalTokens}");
            Console.WriteLine($"Model: {batch.Segments[0].Provenance.ModelId} via {batch.Segments[0].Provenance.EndpointHost} (template {batch.Segments[0].Provenance.PromptTemplateVersion})");
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

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director translate --input <segments.json> --output <translated.json> [--from en] [--to es]");
        Console.WriteLine();
        Console.WriteLine("Batch translate a JSON array of {id, text} segments through the Meta Model API.");
        Console.WriteLine("Each output segment includes translation, provenance (model, host, timestamp, template version), and usage (token counts).");
        Console.WriteLine();
        Console.WriteLine("  --input <path>    Input JSON file: array of {id, text} (required).");
        Console.WriteLine("  --output <path>   Output JSON file to write (required).");
        Console.WriteLine("  --from <code>     Source language code. Default: en");
        Console.WriteLine("  --to <code>       Target language code. Default: es");
        Console.WriteLine("  -h, --help        Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
        Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1 (verified via 'models' command).");
        Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
    }
}

enum TranslateOption
{
    Input,
    Output,
    From,
    To,
}

static class TranslateOptionFlags
{
    private static readonly IReadOnlyDictionary<TranslateOption, string> Flags = new Dictionary<TranslateOption, string>
    {
        [TranslateOption.Input] = "--input",
        [TranslateOption.Output] = "--output",
        [TranslateOption.From] = "--from",
        [TranslateOption.To] = "--to",
    };

    private static readonly IReadOnlyDictionary<string, TranslateOption> ByFlag =
        Flags.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static string ToFlag(TranslateOption option) => Flags[option];

    public static bool TryGet(string flag, out TranslateOption option) => ByFlag.TryGetValue(flag, out option);
}

sealed class TranslateArguments
{
    public string? Input { get; set; }

    public string? Output { get; set; }

    public string From { get; set; } = "en";

    public string To { get; set; } = "es";

    public bool TryApply(TranslateOption option, OptionValue value, out ParseError? error)
    {
        error = null;
        switch (option)
        {
            case TranslateOption.Input:
                Input = value.Text;
                return true;
            case TranslateOption.Output:
                Output = value.Text;
                return true;
            case TranslateOption.From:
                From = value.Text;
                return true;
            case TranslateOption.To:
                To = value.Text;
                return true;
            default:
                error = new ParseError($"Unknown option '{TranslateOptionFlags.ToFlag(option)}' for 'translate'.");
                return false;
        }
    }
}
