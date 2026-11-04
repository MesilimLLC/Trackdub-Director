using Trackdub.Director.Core;

internal static class SmokeCli
{
    private const string DefaultSmokeText = "The director checks every line before it reaches the audience.";

    public static async Task<int> RunAsync(CommandArgs args)
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
                PrintHelp();
                return 0;
            }
            else if (!values[i].StartsWith('-'))
            {
                text = values[i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown option '{values[i]}' for 'smoke'.");
                PrintHelp();
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

    private static void PrintHelp()
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
}
