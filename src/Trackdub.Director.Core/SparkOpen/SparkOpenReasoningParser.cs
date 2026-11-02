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
    /// can be extracted. Every completed think/reasoning block is stripped first, so a verdict
    /// quoted inside reasoning is never taken as the final answer. Strategy, in order:
    ///   1. the last non-blank line, when it parses as a JSON object (the instructed shape);
    ///   2. the last balanced top-level JSON object anywhere in the reply (reasoning that
    ///      quotes earlier braces cannot shadow the trailing answer).
    /// Markdown code fences around the final object are tolerated. A reply whose reasoning ends
    /// inside an unclosed think block yields null, so truncated reasoning never becomes a verdict.
    /// </summary>
    public static string? ExtractFinalJsonObject(string? rawContent)
    {
        return ExtractFinalJsonObject(rawContent, requireEndOfReply: false);
    }

    /// <summary>
    /// <see cref="ExtractFinalJsonObject(string?)"/> with the judge's stricter contract: when
    /// <paramref name="requireEndOfReply"/> is true, a balanced object is the final verdict only if
    /// nothing but whitespace follows its closing brace — a verdict-shaped object quoted in the
    /// reasoning with prose after it is NOT accepted. The translation path keeps the permissive
    /// default, because a plain-text answer needs no trailing-JSON requirement.
    /// </summary>
    public static string? ExtractFinalJsonObject(string? rawContent, bool requireEndOfReply)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        string content = StripTrailingFence(rawContent.Trim());
        content = StripThinkBlocks(content, out bool unterminated);
        if (unterminated)
        {
            // Reasoning was truncated mid-block: the last balanced object may be reasoning prose,
            // so no verdict can be trusted. Take the failed-call path.
            return null;
        }

        string? lastLine = LastNonBlankLine(content);
        if (lastLine is not null && TryWholeObject(lastLine, out string? lineObject))
        {
            return lineObject;
        }

        return LastBalancedObject(content, requireEndOfReply);
    }

    /// <summary>
    /// True when the reply yields a final-answer JSON object. Out parameter carries the
    /// extracted object text, suitable for the standard gate/judge JSON parsers.
    /// </summary>
    public static bool TryExtractFinalJsonObject(string? rawContent, out string finalObject)
    {
        return TryExtractFinalJsonObject(rawContent, out finalObject, requireEndOfReply: false);
    }

    /// <summary>
    /// <see cref="TryExtractFinalJsonObject(string?, out string)"/> with the judge's stricter
    /// end-of-reply contract (see <see cref="ExtractFinalJsonObject(string?, bool)"/>).
    /// </summary>
    public static bool TryExtractFinalJsonObject(string? rawContent, out string finalObject, bool requireEndOfReply)
    {
        string? extracted = ExtractFinalJsonObject(rawContent, requireEndOfReply);
        finalObject = extracted ?? string.Empty;
        return extracted is not null;
    }

    /// <summary>
    /// Extracts the final answer as text, separating it from the reasoning that precedes it. When the
    /// reply ends in a <c>{...}</c> JSON object (a structured answer such as a meaning verdict) that
    /// object is returned so the standard JSON gate parses it; otherwise the remaining non-blank lines
    /// are returned as the answer (an unstructured answer such as a translated line), preserving a
    /// multi-line answer rather than silently dropping every line but the last. Every completed
    /// <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block and trailing code fence is stripped first.
    /// Returns null when nothing remains — a reply with no extractable final answer, which callers
    /// treat as a FAILED call. A reply whose reasoning is truncated inside an unclosed think block is
    /// likewise null, so a half-finished thought can never be returned as the answer.
    /// </summary>
    public static string? ExtractFinalAnswer(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        string content = StripTrailingFence(rawContent.Trim());
        content = StripThinkBlocks(content, out bool unterminated).Trim();
        if (unterminated)
        {
            return null;
        }

        string? jsonObject = ExtractFinalJsonObject(content);
        if (jsonObject is not null)
        {
            return jsonObject;
        }

        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    /// <summary>True when a final answer (JSON object or last line) can be extracted. Out carries it.</summary>
    public static bool TryExtractFinalAnswer(string? rawContent, out string finalAnswer)
    {
        string? extracted = ExtractFinalAnswer(rawContent);
        finalAnswer = extracted ?? string.Empty;
        return extracted is not null;
    }

    /// <summary>
    /// Strips every completed think/reasoning block from the reply, so reasoning prose can never
    /// shadow a plain-text final answer below it. Reasoning models sometimes emit more than one
    /// block, so all completed blocks are removed, not just the first. When generation stops inside
    /// an unclosed think block, the unterminated flag is set true so callers treat the reply as
    /// having no extractable answer. Both spellings of the markers are recognized — bracketed
    /// <c>&lt;thinking&gt;…&lt;/thinking&gt;</c> and the bare line-start <c> thinking</c> /
    /// <c> response</c> — so whichever the deployment emits is handled identically.
    /// </summary>
    private static string StripThinkBlocks(string content, out bool unterminated)
    {
        content = NormalizeBareMarkers(content);

        const string open = "<thinking>";
        const string close = "</thinking>";
        int start = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int end = content.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            content = content.Remove(start, end - start + close.Length);
            start = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        }

        unterminated = start >= 0;
        return content;
    }

    /// <summary>
    /// Rewrites a bare line-start <c> thinking</c> / <c> response</c> marker into the bracketed
    /// <c>&lt;thinking&gt;</c> / <c>&lt;/thinking&gt;</c> form so <see cref="StripThinkBlocks"/>
    /// treats both spellings identically. Only a standalone marker token (followed by whitespace or
    /// the line end) is rewritten, so a translated line that merely begins with the word "thinking"
    /// is never mistaken for a reasoning block.
    /// </summary>
    private static string NormalizeBareMarkers(string content)
    {
        string[] lines = content.Split('\n');
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            string? token = BareMarkerToken(trimmed);
            if (token is null)
            {
                continue;
            }

            string replacement = string.Equals(token, "thinking", StringComparison.OrdinalIgnoreCase) ? "<thinking>" : "</thinking>";
            lines[i] = string.Concat(line.AsSpan(0, line.Length - trimmed.Length), replacement, trimmed.AsSpan(token.Length));
            changed = true;
        }

        return changed ? string.Join('\n', lines) : content;
    }

    /// <summary>
    /// "thinking" or "response" when <paramref name="trimmed"/> starts with that bare marker token
    /// as a standalone word, else null.
    /// </summary>
    private static string? BareMarkerToken(string trimmed)
    {
        if (trimmed.StartsWith("thinking", StringComparison.OrdinalIgnoreCase) && IsStandaloneToken(trimmed, "thinking"))
        {
            return "thinking";
        }

        if (trimmed.StartsWith("response", StringComparison.OrdinalIgnoreCase) && IsStandaloneToken(trimmed, "response"))
        {
            return "response";
        }

        return null;
    }

    private static bool IsStandaloneToken(string trimmed, string token)
        => trimmed.Length == token.Length || char.IsWhiteSpace(trimmed[token.Length]);

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
    /// snippet cannot unbalance the scan. An opening brace with no matching close is skipped and
    /// the scan continues, so a stray <c>{</c> in the reasoning cannot shadow a later valid object.
    /// When <paramref name="requireEndOfReply"/> is true, only an object whose closing brace reaches
    /// the end of the trimmed content is accepted (the judge's strict contract).
    /// </summary>
    private static string? LastBalancedObject(string content, bool requireEndOfReply)
    {
        string? last = null;
        int trimmedLength = requireEndOfReply ? content.TrimEnd().Length : -1;
        int i = 0;
        while (i < content.Length)
        {
            if (content[i] == '{')
            {
                int end = FindObjectEnd(content, i);
                if (end < 0)
                {
                    i++;
                    continue;
                }

                if (requireEndOfReply && end < trimmedLength)
                {
                    // Verdict-shaped object with prose after it is reasoning, not the final verdict.
                    i = end;
                    continue;
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
