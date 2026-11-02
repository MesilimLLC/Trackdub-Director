using System.Text.Json;
using Trackdub.Director.Core;

const string DefaultSmokeText = "The director checks every line before it reaches the audience.";

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    var command = new CommandArgs(args);
    if (command.Count == 0)
    {
        PrintUsage();
        return 1;
    }

    string rawVerb = command.Values[0];
    string verb = rawVerb.ToLowerInvariant();
    if (verb is "-h" or "--help" or "help")
    {
        PrintUsage();
        return 0;
    }

    return await DispatchAsync(verb, rawVerb, command.Rest()).ConfigureAwait(false);
}

static Task<int> DispatchAsync(string verb, string rawVerb, CommandArgs args)
{
    var commands = new Dictionary<string, Func<CommandArgs, Task<int>>>(StringComparer.Ordinal)
    {
        ["smoke"] = RunSmokeAsync,
        ["translate"] = RunTranslateAsync,
        ["models"] = RunModelsAsync,
        ["qa"] = RunQaAsync,
        ["ingest"] = RunIngestAsync,
        ["pipeline"] = RunPipelineAsync,
    };

    if (commands.TryGetValue(verb, out Func<CommandArgs, Task<int>>? command))
    {
        return command(args);
    }

    return RunEvidenceCommandAsync(verb, rawVerb, args);
}

static Task<int> RunEvidenceCommandAsync(string verb, string rawVerb, CommandArgs args)
{
    return verb switch
    {
        "sign-shot" => Task.FromResult(SignShotCli.Run(args.ToArray())),
        "meaning-eval" => MeaningEvalCli.RunAsync(args.ToArray()),
        "spark-open" => SparkOpenCli.RunAsync(args.ToArray()),
        "verify-only" => Task.FromResult(VerifyOnlyCli.Run(args.ToArray())),
        "recover" => RecoverCli.RunAsync(args.ToArray()),
        _ => Task.FromResult(UnknownCommand(rawVerb)),
    };
}

static int UnknownCommand(string raw)
{
    Console.Error.WriteLine($"Unknown command '{raw}'. Expected 'smoke', 'translate', 'models', 'qa', 'ingest', 'pipeline', 'sign-shot', 'meaning-eval', 'spark-open', 'verify-only', or 'recover'.");
    PrintUsage();
    return 1;
}

static Task<int> RunQaAsync(CommandArgs args) => QaCli.RunAsync(args.ToArray());

static Task<int> RunIngestAsync(CommandArgs args) => IngestCli.RunAsync(args.ToArray());

static Task<int> RunPipelineAsync(CommandArgs args) => PipelineCli.RunAsync(args.ToArray());

static async Task<int> RunSmokeAsync(CommandArgs args)
{
    string text = DefaultSmokeText;
    IReadOnlyList<string> values = args.Values;
    for (int i = 0; i < values.Count; i++)
    {
        if (values[i] == "--text" && i + 1 < values.Count)
        {
            text = values[i + 1];
            i++;
        }
        else if (values[i] is "-h" or "--help")
        {
            PrintSmokeHelp();
            return 0;
        }
        else if (!values[i].StartsWith('-'))
        {
            text = values[i];
        }
        else
        {
            Console.Error.WriteLine($"Unknown option '{values[i]}' for 'smoke'.");
            PrintSmokeHelp();
            return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(text))
    {
        Console.Error.WriteLine("error: --text must be a non-empty line.");
        return 1;
    }

    try
    {
        using var client = new MetaClient();
        string result = await client.TranslateToSpanishAsync(text).ConfigureAwait(false);
        Console.WriteLine($"EN: {text}");
        Console.WriteLine($"ES: {result}");
        return 0;
    }
    catch (MetaApiException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunTranslateAsync(CommandArgs args)
{
    ParseResult<TranslateArguments> parsed = TryParseTranslateArgs(args);
    if (parsed.Status == ParseStatus.Help)
    {
        PrintTranslateHelp();
        return 0;
    }

    if (parsed.Status == ParseStatus.Error)
    {
        Console.Error.WriteLine(parsed.Error!.Value);
        PrintTranslateHelp();
        return 1;
    }

    if (!TryResolveTranslatePaths(parsed.Value!, out FilePath input, out FilePath output))
    {
        return 1;
    }

    return await ExecuteTranslateAsync(parsed.Value!, input, output).ConfigureAwait(false);
}

static ParseResult<TranslateArguments> TryParseTranslateArgs(CommandArgs args)
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
            return ParseResult<TranslateArguments>.Failure(error!);
        }

        i++;
    }

    return ParseResult<TranslateArguments>.Success(result);
}

static bool TryResolveTranslatePaths(TranslateArguments parsed, out FilePath input, out FilePath output)
{
    input = new FilePath(string.Empty);
    output = new FilePath(string.Empty);

    if (string.IsNullOrWhiteSpace(parsed.Input))
    {
        Console.Error.WriteLine("error: --input <segments.json> is required.");
        PrintTranslateHelp();
        return false;
    }

    if (string.IsNullOrWhiteSpace(parsed.Output))
    {
        Console.Error.WriteLine("error: --output <translated.json> is required.");
        PrintTranslateHelp();
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

static async Task<int> ExecuteTranslateAsync(TranslateArguments parsed, FilePath input, FilePath output)
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

static async Task<int> RunModelsAsync(CommandArgs args)
{
    if (args.Count > 0)
    {
        if (args.Values[0] is "-h" or "--help")
        {
            PrintModelsHelp();
            return 0;
        }

        Console.Error.WriteLine($"Unknown option '{args.Values[0]}' for 'models'.");
        PrintModelsHelp();
        return 1;
    }

    try
    {
        using var client = new MetaClient();
        IReadOnlyList<MetaClient.ModelInfo> models = await client.ListModelsAsync().ConfigureAwait(false);

        Console.WriteLine($"Available models ({models.Count}):");
        foreach (MetaClient.ModelInfo m in models)
        {
            Console.WriteLine($"  {m.Id} (owned_by: {m.OwnedBy})");
        }

        string llama = models.FirstOrDefault(m => m.Id.Contains("llama", StringComparison.OrdinalIgnoreCase))?.Id ?? "(none)";
        Console.WriteLine();
        Console.WriteLine($"Llama model IDs: {string.Join(", ", models.Where(m => m.Id.Contains("llama", StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).DefaultIfEmpty("(none)"))}");
        Console.WriteLine($"Default META_MODEL: {MetaAuth.GetModel()} ({(models.Any(m => m.Id == MetaAuth.GetModel()) ? "verified against account" : "not in listed models, may still be valid")})");
        Console.WriteLine($"Preferred Llama for translation default: {llama}");

        var json = JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine();
        Console.WriteLine(json);
        return 0;
    }
    catch (MetaApiException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: trackdub-director <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  smoke      Smoke test: one real Meta Model API call, English to Spanish.");
    Console.WriteLine("  translate  Batch translate a segments JSON file.");
    Console.WriteLine("  models     List models available to this account (GET /models).");
    Console.WriteLine("  qa         Translate + timing QA loop (bounce-back revisions with report).");
    Console.WriteLine("  ingest     Transcribe a WAV file (ASR diarization) into a segments JSON.");
    Console.WriteLine("  pipeline   Run the lanes in order: transcribe (with --audio), translation + QA, scene, audition (opt-in).");
    Console.WriteLine("  sign-shot  Extend a localised sign frame across a shot, gated on the shot being static.");
    Console.WriteLine("  meaning-eval  Score the meaning gate on labeled cases (catch rate, false alarms).");
    Console.WriteLine("  spark-open    Opt-in Azure Muse Spark Open lane: judge / production / translation / smoke.");
    Console.WriteLine("  verify-only   Zero-call gate over a captured run: recompute verdicts, write a hash-sealed receipt.");
    Console.WriteLine("  recover       Glimmer recovery agent over refused candidates: Glimmer proposes, the existing gates dispose.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -h, --help   Show help.");
    Console.WriteLine();
    Console.WriteLine("Run 'trackdub-director <command> --help' for command details.");
}

static void PrintSmokeHelp()
{
    Console.WriteLine("Usage: trackdub-director smoke [--text <line>]");
    Console.WriteLine();
    Console.WriteLine("Smoke test: one real Meta Model API call, English to Spanish.");
    Console.WriteLine();
    Console.WriteLine($"  --text <line>   English line to translate. Default: \"{DefaultSmokeText}\"");
    Console.WriteLine("  -h, --help      Show this help.");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required. Never committed; see .env.example.");
    Console.WriteLine("  META_MODEL                              Default: muse-spark-1.1");
    Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
}

static void PrintTranslateHelp()
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

static void PrintModelsHelp()
{
    Console.WriteLine("Usage: trackdub-director models");
    Console.WriteLine();
    Console.WriteLine("List models available to this account via GET /models.");
    Console.WriteLine("Reports Llama model IDs and whether the current META_MODEL default is among them.");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  META_API_KEY (or META_MODEL_API_KEY)   Required.");
    Console.WriteLine("  META_MODEL_API_BASE_URL                 Default: https://api.meta.ai/v1");
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
