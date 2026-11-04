using Trackdub.Director.Core;

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
        ["smoke"] = SmokeCli.RunAsync,
        ["translate"] = TranslateCli.RunAsync,
        ["models"] = ModelsCli.RunAsync,
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
