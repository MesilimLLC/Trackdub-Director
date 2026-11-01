using System.Text.Json;
using Trackdub.Director.Core;

internal static class VerifyOnlyCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string runDir = VerifyOnlyService.DefaultRunRelativePath;
        string? outPath = null;
        string? htmlPath = null;
        bool hasRunPositional = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run" when i + 1 < args.Length:
                    runDir = args[i + 1];
                    i++;
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[i + 1];
                    i++;
                    break;
                case "--html" when i + 1 < args.Length:
                    htmlPath = args[i + 1];
                    i++;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    if (!args[i].StartsWith('-') && !hasRunPositional)
                    {
                        runDir = args[i];
                        hasRunPositional = true;
                        break;
                    }

                    Console.Error.WriteLine($"Unknown option '{args[i]}' for 'verify-only'.");
                    PrintHelp();
                    return 2;
            }
        }

        string resolvedRun;
        try
        {
            resolvedRun = Path.GetFullPath(runDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"error: invalid run directory: {ex.Message}");
            return 2;
        }

        try
        {
            var service = new VerifyOnlyService();
            VerifyOnlyResult result = service.VerifyRun(resolvedRun);
            string receiptPath = outPath is null
                ? Path.Combine(resolvedRun, VerifyOnlyService.DefaultReceiptFileName)
                : Path.GetFullPath(outPath);
            string receiptHtmlPath = htmlPath is null
                ? Path.Combine(
                    Path.GetDirectoryName(receiptPath) ?? resolvedRun,
                    Path.GetFileNameWithoutExtension(receiptPath) + ".html")
                : Path.GetFullPath(htmlPath);

            await VerifyOnlyService.WriteReceiptAsync(receiptPath, result.Receipt).ConfigureAwait(false);
            await VerifyOnlyService.WriteReceiptHtmlAsync(receiptHtmlPath, result.Receipt).ConfigureAwait(false);

            PrintSummary(result.Receipt, receiptPath, receiptHtmlPath);
            return result.ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"error: unreadable input: {ex.Message}");
            return 2;
        }
    }

    private static void PrintSummary(VerifyOnlyReceipt receipt, string receiptPath, string htmlPath)
    {
        Console.WriteLine($"Verify-only: {receipt.OverallVerdict} (exit {receipt.ExitStatus}) | segments {receipt.Segments.Length} | tolerance {receipt.ToleranceFraction:P0}");
        foreach (VerifyOnlySegmentReceipt seg in receipt.Segments)
        {
            string reasons = seg.Reasons.Length == 0 ? "" : $" [{string.Join(", ", seg.Reasons)}]";
            string media = seg.MediaPresent ? "media found" : "media absent";
            string hashLabel = seg.WinningMediaHash is null
                ? ""
                : $" hash={ProvenanceLabel(seg.WinningMediaHash.Provenance)}";
            Console.WriteLine($"  {seg.Id}: {seg.Verdict}{reasons} | {media}{hashLabel}");
        }

        Console.WriteLine($"Receipt (hash-sealed) written to {receiptPath}");
        Console.WriteLine($"Receipt HTML written to {htmlPath}");
    }

    private static string ProvenanceLabel(VerifyOnlyHashProvenance provenance) => provenance switch
    {
        VerifyOnlyHashProvenance.Recomputed => "recomputed",
        VerifyOnlyHashProvenance.RecordedAtCapture => "recorded-at-capture",
        _ => provenance.ToString(),
    };

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: trackdub-director verify-only [<run-dir>] [--run <run-dir>] [--out <receipt.json>] [--html <receipt.html>]");
        Console.WriteLine();
        Console.WriteLine("Offline CI-style gate over a captured run's manifests. Recomputes verdicts from");
        Console.WriteLine("recorded numbers; never trusts a summary field; a check that was not run never");
        Console.WriteLine("counts as a pass. Zero live model or TTS calls.");
        Console.WriteLine();
        Console.WriteLine($"  <run-dir> / --run   Captured run folder. Default: {VerifyOnlyService.DefaultRunRelativePath}");
        Console.WriteLine("  --out <path>        Receipt JSON path. Default: <run-dir>/verify-only-receipt.json");
        Console.WriteLine("  --html <path>       Receipt HTML path. Default: beside the JSON as .html");
        Console.WriteLine("  -h, --help          Show this help.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 = every segment Verified; 1 = at least one HumanReviewRequired;");
        Console.WriteLine("2 = usage error or unreadable/invalid input. The receipt is hash-sealed; it is");
        Console.WriteLine("never called signed, certified, or attested.");
    }
}
