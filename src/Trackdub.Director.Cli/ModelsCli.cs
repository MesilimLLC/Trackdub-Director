using System.Text.Json;
using Trackdub.Director.Core;

internal static class ModelsCli
{
    public static async Task<int> RunAsync(CommandArgs args)
    {
        if (args.Count > 0)
        {
            if (args.Values[0] is "-h" or "--help")
            {
                PrintHelp();
                return 0;
            }

            Console.Error.WriteLine($"Unknown option '{args.Values[0]}' for 'models'.");
            PrintHelp();
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

    private static void PrintHelp()
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
}
