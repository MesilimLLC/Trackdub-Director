using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Extracts the final answer from a Muse Spark Open reply. Spark Open always reasons,
/// so every reply carries reasoning output before the answer. The lane instructs the
/// model to end its reply with the answer as a single JSON object on the final line;
/// this parser pulls that object out, tolerating the reasoning prose that precedes it.
///
/// A reply whose final-answer JSON object cannot be extracted is a FAILED call: callers
/// record it as failed with its usage and never guess at an answer or silently re-prompt.
/// </summary>
public static class SparkOpenReasoningParser
{
    /// <summary>
    /// Returns the balanced JSON object that carries the final answer, or null when none
    /// can be extracted. Strategy, in order:
    ///   1. the last non-blank line, when it parses as a JSON object (the instructed shape);
    ///   2. the last balanced top-level JSON object anywhere in the reply (reasoning that
    ///      quotes earlier braces cannot shadow the trailing answer).
    /// Markdown code fences around the final object are tolerated.
    /// </summary>
    public static string? ExtractFinalJsonObject(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        string content = StripTrailingFence(rawContent.Trim());

        string? lastLine = LastNonBlankLine(content);
        if (lastLine is not null && TryWholeObject(lastLine, out string? lineObject))
        {
            return lineObject;
        }

        return LastBalancedObject(content);
    }

    /// <summary>
    /// True when the reply yields a final-answer JSON object. Out parameter carries the
    /// extracted object text, suitable for the standard gate/judge JSON parsers.
    /// </summary>
    public static bool TryExtractFinalJsonObject(string? rawContent, out string finalObject)
    {
        string? extracted = ExtractFinalJsonObject(rawContent);
        finalObject = extracted ?? string.Empty;
        return extracted is not null;
    }

    /// <summary>
    /// Extracts the final answer as text, separating it from the reasoning that precedes it. When the
    /// reply ends in a <c>{...}</c> JSON object (a structured answer such as a meaning verdict) that
    /// object is returned so the standard JSON gate parses it; otherwise the last non-blank line is
    /// returned (an unstructured answer such as a translated line). Any leading <c>&lt;think&gt;…&lt;/think&gt;</c>
    /// reasoning block and trailing code fence are stripped first. Returns null when nothing remains —
    /// a reply with no extractable final answer, which callers treat as a FAILED call.
    /// </summary>
    public static string? ExtractFinalAnswer(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        string content = StripThinkBlock(StripTrailingFence(rawContent.Trim())).Trim();

        string? jsonObject = ExtractFinalJsonObject(content);
        if (jsonObject is not null)
        {
            return jsonObject;
        }

        string? lastLine = LastNonBlankLine(content);
        return string.IsNullOrWhiteSpace(lastLine) ? null : lastLine;
    }

    /// <summary>True when a final answer (JSON object or last line) can be extracted. Out carries it.</summary>
    public static bool TryExtractFinalAnswer(string? rawContent, out string finalAnswer)
    {
        string? extracted = ExtractFinalAnswer(rawContent);
        finalAnswer = extracted ?? string.Empty;
        return extracted is not null;
    }

    private static string StripThinkBlock(string content)
    {
        // Remove a leading <think>...</think> reasoning block if present, so a plain-text final answer
        // is not shadowed by reasoning lines above it.
        const string open = "<think>";
        const string close = "</think>";
        int start = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return content;
        }

        int end = content.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? content : content.Remove(start, end - start + close.Length);
    }

    private static string StripTrailingFence(string content)
    {
        // Drop any line that is only a code-fence marker (``` or ```lang), so a final object wrapped in
        // a fenced block is seen on its own line without losing the object between the fences.
        string[] lines = content.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            string trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept).Trim();
    }

    private static string? LastNonBlankLine(string content)
    {
        string[] lines = content.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim().TrimEnd('\r').Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    private static bool TryWholeObject(string candidate, out string? result)
    {
        string trimmed = candidate.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    result = trimmed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Scans for the last balanced top-level JSON object and returns it when it parses as an
    /// object. String literals and escapes are tracked so a brace inside a quoted reasoning
    /// snippet cannot unbalance the scan.
    /// </summary>
    private static string? LastBalancedObject(string content)
    {
        string? last = null;
        int i = 0;
        while (i < content.Length)
        {
            if (content[i] == '{')
            {
                int end = FindObjectEnd(content, i);
                if (end < 0)
                {
                    break;
                }

                string candidate = content[i..end];
                if (TryWholeObject(candidate, out string? parsed))
                {
                    last = parsed;
                }

                i = end;
            }
            else
            {
                i++;
            }
        }

        return last;
    }

    private static int FindObjectEnd(string content, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i + 1;
            }
        }

        return -1;
    }
}
