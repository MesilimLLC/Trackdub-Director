using System.Text;
using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Writes the recovery run's two Phase-2 outputs into samples/glimmer-recovery/:
/// recovery-report.json (the full trace) and NOTES.md (a human summary, including how the
/// leakage rule was enforced). Both are written atomically (temp + move) so an interrupted
/// write never leaves a partial file. No API key or route secret is ever written.
/// </summary>
public static class RecoveryOutputWriter
{
    public const string ReportFileName = "recovery-report.json";
    public const string NotesFileName = "NOTES.md";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task WriteReportAsync(string outputFolder, RecoveryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(outputFolder);
        string path = Path.Combine(outputFolder, ReportFileName);
        await WriteAtomicallyAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteNotesAsync(string outputFolder, RecoveryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(outputFolder);
        string path = Path.Combine(outputFolder, NotesFileName);
        await WriteAtomicallyAsync(path, BuildNotes(report), cancellationToken).ConfigureAwait(false);
    }

    private static string BuildNotes(RecoveryReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Glimmer recovery run notes");
        sb.AppendLine();
        sb.AppendLine($"- Direction: {report.SourceLanguage} -> {report.TargetLanguage}");
        sb.AppendLine($"- Glimmer model: {report.GlimmerModel} (reasoning_effort {report.GlimmerReasoningEffort})");
        sb.AppendLine($"- Meaning gate: {report.MeaningModel} (template {report.MeaningPromptTemplateVersion})");
        sb.AppendLine($"- Attempt cap per item: {report.MaxAttemptsPerItem}");
        sb.AppendLine(
            $"- Recovery (per attempt): {report.Totals.Recovered}/{report.Totals.TotalItems} items recovered.");
        sb.AppendLine(
            $"- Recovery (per distinct line): {report.Totals.DistinctLinesRecovered}/{report.Totals.DistinctLines} lines recovered " +
            "(a line counts as recovered if any of its refused attempts recovered; reported separately so the rate is not double-counted).");
        sb.AppendLine(
            $"- Budget: Glimmer calls {report.Totals.GlimmerCalls}/{report.Totals.GlimmerCallCap}, " +
            $"Meta calls {report.Totals.MetaCalls}/{report.Totals.MetaCallCap}.");
        sb.AppendLine();
        sb.AppendLine("## Route (read from env only; recorded here as observed values)");
        sb.AppendLine();
        sb.AppendLine("The client reads GLIMMER_BASE_URL / GLIMMER_API_KEY / GLIMMER_MODEL and the Meta key");
        sb.AppendLine("from the environment only; nothing below is hardcoded into a code path. GLIMMER_BASE_URL");
        sb.AppendLine("must be the OpenAI-compatible base ending in `/openai/v1` (the client appends");
        sb.AppendLine("`/chat/completions` itself; passing the full `.../chat/completions` URL would double the path).");
        sb.AppendLine($"The Glimmer model string recorded for this run was `{report.GlimmerModel}`");
        sb.AppendLine($"(reasoning_effort {report.GlimmerReasoningEffort}). The pre-flight probe result is");
        sb.AppendLine("recorded below; the run proceeded to the loop only after the probe returned");
        sb.AppendLine("{\"ok\": true} from the requested Glimmer model.");
        if (report.Probe is not null)
        {
            sb.AppendLine();
            sb.AppendLine("### Pre-flight probe");
            sb.AppendLine();
            sb.AppendLine($"- Endpoint host: `{report.Probe.Host}` (the key is never recorded)");
            sb.AppendLine($"- Requested model: `{report.Probe.RequestedModel}`");
            sb.AppendLine($"- Returned model: `{report.Probe.ReturnedModel}`");
            sb.AppendLine(
                $"- Usage: prompt={report.Probe.PromptTokens} completion={report.Probe.CompletionTokens} total={report.Probe.TotalTokens}");
            sb.AppendLine($"- Wall seconds: {report.Probe.WallSeconds:0.000}");
        }

        if (report.RunError is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Run failure");
            sb.AppendLine();
            sb.AppendLine($"The run stopped early after a provider failure; the completed items below are preserved.");
            sb.AppendLine($"- Error: {report.RunError}");
        }

        sb.AppendLine();
        sb.AppendLine("## Leakage rule");
        sb.AppendLine();
        sb.AppendLine("Each item is built from exactly ONE refused attempt record. The agent's input is only");
        sb.AppendLine("the source line, the window and tolerance, the recorded gate failure reasons for that");
        sb.AppendLine("attempt, and any captured scene context. No sibling attempt's text for the same segment");
        sb.AppendLine("(later revision, retry, or accepted candidate) is ever included, and the set is built");
        sb.AppendLine("from the captured QA reports (gate outputs), never from the seeded meaning-eval labels.");
        sb.AppendLine();
        sb.AppendLine("## Per-item outcomes");
        sb.AppendLine();
        foreach (RecoveryItemReport item in report.Items)
        {
            sb.AppendLine(
                $"- `{item.SegmentId}` att#{item.AttemptNumber} ({item.SourceFile}, {item.FailureType}, " +
                $"window {item.WindowSeconds:0.00}s): {item.Outcome}" +
                (item.RecoveredText is null ? string.Empty : $" -> \"{item.RecoveredText}\"") +
                $" [{item.Attempts.Length} attempt(s)]");
        }

        return sb.ToString();
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }
}
