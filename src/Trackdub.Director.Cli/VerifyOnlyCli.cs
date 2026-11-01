using System.Globalization;
using Trackdub.Director.Core;

/// <summary>
/// verify-only: a CI-style gate over a captured run. Zero network calls; verdicts are recomputed from the
/// recorded numbers. Exit 0 = every line Verified, 1 = at least one line needs a human, 2 = usage error or
/// unreadable input.
/// </summary>
internal static class VerifyOnlyCli
{
    internal const string DefaultRunFolder = "samples/voa10";
    private const string ReceiptJsonName = "verify-receipt.json";
    private const int ExitUsage = 2;

    private sealed record Options(string RunFolder, string? MediaDir, string? Out, string? Html, double Tolerance);

    public static int Run(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        Options? options = Parse(args, out string? error);
        if (options is null)
        {
            return Usage(error!);
        }

        try
        {
            VerifyReceipt receipt = VerifyOnlyService.Verify(options.RunFolder, new VerifyOnlyOptions(options.MediaDir, options.Tolerance));
            (string jsonPath, string htmlPath) = WriteReceipt(options, receipt);
            PrintSummary(receipt, jsonPath, htmlPath);
            return receipt.ExitStatus;
        }
        catch (Exception ex) when (ex is VerifyInputException or IOException or UnauthorizedAccessException)
        {
            return Usage(ex.Message);
        }
    }

    private static (string Json, string Html) WriteReceipt(Options options, VerifyReceipt receipt)
    {
        string jsonPath = Path.GetFullPath(options.Out ?? Path.Combine(options.RunFolder, ReceiptJsonName));
        string htmlPath = Path.GetFullPath(options.Html ?? Path.ChangeExtension(jsonPath, ".html"));
        string json = receipt.ToJson();
        WriteAtomically(jsonPath, json);
        // The page is rendered from the JSON that was just written, so it can never show more than the receipt says.
        WriteAtomically(htmlPath, VerifyReceiptHtml.Render(VerifyReceipt.FromJson(json)));
        return (jsonPath, htmlPath);
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static Options? Parse(string[] args, out string? error)
    {
        string? runFolder = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            bool isFlag = args[i].StartsWith("--", StringComparison.Ordinal);
            if (!isFlag && runFolder is null)
            {
                runFolder = args[i];
                continue;
            }

            error = isFlag ? ValidateFlag(args, i) : $"Unexpected argument '{args[i]}' for 'verify-only'.";
            if (error is not null)
            {
                return null;
            }

            values[args[i]] = args[++i];
        }

        return BuildOptions(runFolder ?? DefaultRunFolder, values, out error);
    }

    private static Options? BuildOptions(string runFolder, Dictionary<string, string> values, out string? error)
    {
        double tolerance = QaOptions.Default.ToleranceFraction;
        if (values.TryGetValue("--tolerance", out string? raw)
            && (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance) || !double.IsFinite(tolerance) || tolerance < 0))
        {
            error = "--tolerance must be a non-negative number (a fraction, e.g. 0.10).";
            return null;
        }

        error = null;
        return new Options(runFolder, values.GetValueOrDefault("--media-dir"), values.GetValueOrDefault("--out"), values.GetValueOrDefault("--html"), tolerance);
    }

    private static string? ValidateFlag(string[] args, int i)
    {
        string[] known = ["--media-dir", "--out", "--html", "--tolerance"];
        string? value = i + 1 < args.Length ? args[i + 1] : null;
        return !known.Contains(args[i]) ? $"Unexpected argument '{args[i]}' for 'verify-only'."
            : string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? $"Missing value for '{args[i]}'."
            : null;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine("Run 'trackdub-director verify-only --help' for usage.");
        return ExitUsage;
    }

    private static void PrintSummary(VerifyReceipt receipt, string jsonPath, string htmlPath)
    {
        Console.WriteLine($"verify-only: {receipt.RunFolder} ({receipt.SegmentsTotal} lines, tolerance {VerifyOnlyService.Percent(receipt.ToleranceFraction)})");
        foreach (SegmentReceipt segment in receipt.Segments)
        {
            string reasons = segment.Reasons.Length == 0 ? string.Empty : $" [{string.Join(", ", segment.Reasons)}]";
            Console.WriteLine($"  {segment.Id}: {segment.Verdict}{reasons}");
        }

        Console.WriteLine($"{receipt.SegmentsVerified} of {receipt.SegmentsTotal} verified: {receipt.Verdict} (exit {receipt.ExitStatus})");
        Console.WriteLine($"Receipt JSON: {jsonPath}");
        Console.WriteLine($"Receipt HTML: {htmlPath}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"Usage: trackdub-director verify-only [<run-folder>] [--media-dir <dir>] [--out <receipt.json>] [--html <receipt.html>] [--tolerance <fraction>]");
        Console.WriteLine();
        Console.WriteLine($"Gate over a captured run (default {DefaultRunFolder}). Reads {AuditionOutputWriter.ManifestFileName}; makes zero network calls; recomputes every verdict.");
        Console.WriteLine("A line is Verified only if it has a winning take, that take re-passes the QA duration check against the stamped");
        Console.WriteLine("window, a meaning Pass is recorded for its text, and its media file is on disk. Otherwise it is HumanReviewRequired,");
        Console.WriteLine("with reasons Uncast, TimingFail, MeaningFail, MeaningNotEvaluated, MediaNotPresent (or MediaHashMismatch).");
        Console.WriteLine();
        Console.WriteLine("  --media-dir <dir>    Folder holding the take WAVs. Default: <run-folder>/media/audition");
        Console.WriteLine($"  --out <path>         Receipt JSON path. Default: <run-folder>/{ReceiptJsonName}");
        Console.WriteLine("  --html <path>        Receipt HTML path. Default: the JSON path with .html");
        Console.WriteLine("  --tolerance <f>      Timing tolerance fraction. Default: 0.10 (the QA default)");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 all Verified; 1 at least one HumanReviewRequired; 2 usage error or unreadable input.");
    }
}
