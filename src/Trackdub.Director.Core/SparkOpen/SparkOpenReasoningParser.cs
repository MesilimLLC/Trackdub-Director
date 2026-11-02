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
    /// True when the reply yields a final-answer JSON object. Out parameter carries the
    /// extracted object text, suitable for the standard gate/judge JSON parsers. When
    /// <paramref name="requireEndOfReply"/> is true, a balanced object is the final verdict only
    /// if nothing but whitespace follows its closing brace (the judge's strict contract).
    /// </summary>
    public static bool TryExtractFinalJsonObject(string? rawContent, out string finalObject, bool requireEndOfReply = false)
    {
        ReplyText? extracted = rawContent is null
            ? null
            : ExtractFinalJsonObject(new ReplyText(rawContent), requireEndOfReply ? ExtractionMode.RequireEndOfReply : ExtractionMode.Permissive);
        finalObject = extracted?.Value ?? string.Empty;
        return extracted is not null;
    }

    /// <summary>
    /// Extracts the final answer as text, separating it from the reasoning that precedes it. When the
    /// reply ends in a <c>{...}</c> JSON object (a structured answer such as a meaning verdict) that
    /// object is returned so the standard JSON gate parses it; otherwise the remaining non-blank lines
    /// are returned as the answer (an unstructured answer such as a translated line), preserving a
    /// multi-line answer rather than silently dropping every line but the last. Every completed
    /// <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block and trailing code fence is stripped first.
    /// Returns false when nothing remains — a reply with no extractable final answer, which callers
    /// treat as a FAILED call. A reply whose reasoning is truncated inside an unclosed think block is
    /// likewise false, so a half-finished thought can never be returned as the answer.
    /// </summary>
    public static bool TryExtractFinalAnswer(string? rawContent, out string finalAnswer)
    {
        ReplyText? extracted = rawContent is null ? null : ExtractFinalAnswer(new ReplyText(rawContent));
        finalAnswer = extracted?.Value ?? string.Empty;
        return extracted is not null;
    }

    private static ReplyText? ExtractFinalJsonObject(ReplyText rawContent, ExtractionMode mode)
    {
        if (string.IsNullOrWhiteSpace(rawContent.Value))
        {
            return null;
        }

        ReplyText? prepared = Prepare(rawContent);
        if (prepared is null)
        {
            return null;
        }

        ReplyText content = prepared.Value;
        ReplyText? lastLine = LastNonBlankLine(content);
        if (lastLine is not null && TryWholeObject(lastLine.Value, out ReplyText? lineObject))
        {
            return lineObject;
        }

        return LastBalancedObject(content, mode)?.Object;
    }

    private static ReplyText? ExtractFinalAnswer(ReplyText rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent.Value))
        {
            return null;
        }

        ReplyText? prepared = Prepare(rawContent);
        if (prepared is null)
        {
            return null;
        }

        ReplyText content = prepared.Value;
        ReplyText? lastLine = LastNonBlankLine(content);
        if (lastLine is not null && TryWholeObject(lastLine.Value, out ReplyText? lineObject))
        {
            // A whole-object final line is the structured answer (a translation JSON or a meaning verdict).
            return lineObject;
        }

        FoundObject? finalObject = LastBalancedObject(content, ExtractionMode.RequireEndOfReply);
        if (finalObject is not null)
        {
            // A balanced object that ends the reply is the answer.
            return finalObject.Value.Object;
        }

        // A JSON object embedded in prose (an example the model quoted before giving the real answer)
        // must not displace that answer: the trailing plain text after the last embedded object wins.
        FoundObject? embedded = LastBalancedObject(content, ExtractionMode.Permissive);
        if (embedded is not null && embedded.Value.End < content.Value.TrimEnd().Length)
        {
            string tail = content.Value[embedded.Value.End..].Trim();
            if (tail.Length > 0)
            {
                return new ReplyText(tail);
            }
        }

        return string.IsNullOrWhiteSpace(content.Value) ? null : content.Trimmed();
    }

    /// <summary>
    /// Strips a trailing code fence and every completed think/reasoning block. Returns null when the
    /// reply ended inside an unclosed think block, so truncated reasoning is never taken as an answer.
    /// </summary>
    private static ReplyText? Prepare(ReplyText rawContent)
    {
        ReplyText content = StripTrailingFence(rawContent.Trimmed());
        ThinkStripResult stripped = StripThinkBlocks(content);
        return stripped.Status == ThinkStatus.Unterminated ? null : stripped.Content;
    }

    /// <summary>
    /// Strips every completed think/reasoning block from the reply, so reasoning prose can never
    /// shadow a plain-text final answer below it. Reasoning models sometimes emit more than one
    /// block, so all completed blocks are removed, not just the first. When generation stops inside
    /// an unclosed think block, the status is Unterminated so callers treat the reply as having no
    /// extractable answer. Both spellings of the markers are recognized — bracketed
    /// <c>&lt;thinking&gt;…&lt;/thinking&gt;</c> and the bare line-start <c> thinking</c> /
    /// <c> response</c> — so whichever the deployment emits is handled identically.
    /// </summary>
    private static ThinkStripResult StripThinkBlocks(ReplyText content)
    {
        ReplyText normalized = NormalizeBareMarkers(content);
        string text = normalized.Value;

        const string open = "<thinking>";
        const string close = "</thinking>";
        int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int end = text.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            text = text.Remove(start, end - start + close.Length);
            start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        }

        return new ThinkStripResult(new ReplyText(text), start >= 0 ? ThinkStatus.Unterminated : ThinkStatus.Complete);
    }

    /// <summary>
    /// Rewrites a bare line-start <c> thinking</c> / <c> response</c> marker into the bracketed
    /// <c>&lt;thinking&gt;</c> / <c>&lt;/thinking&gt;</c> form so <see cref="StripThinkBlocks"/>
    /// treats both spellings identically. Only a standalone marker token (followed by whitespace or
    /// the line end) is rewritten, so a translated line that merely begins with the word "thinking"
    /// is never mistaken for a reasoning block.
    /// </summary>
    private static ReplyText NormalizeBareMarkers(ReplyText content)
    {
        string[] lines = content.Value.Split('\n');
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            Marker? token = BareMarkerToken(new ReplyText(trimmed));
            if (token is null)
            {
                continue;
            }

            string markerText = MarkerText(token.Value);
            string replacement = token == Marker.Thinking ? "<thinking>" : "</thinking>";
            lines[i] = string.Concat(line.AsSpan(0, line.Length - trimmed.Length), replacement, trimmed.AsSpan(markerText.Length));
            changed = true;
        }

        return changed ? new ReplyText(string.Join('\n', lines)) : content;
    }

    private static Marker? BareMarkerToken(ReplyText trimmed)
    {
        if (StartsWithToken(trimmed, Marker.Thinking))
        {
            return Marker.Thinking;
        }

        if (StartsWithToken(trimmed, Marker.Response))
        {
            return Marker.Response;
        }

        return null;
    }

    private static bool StartsWithToken(ReplyText trimmed, Marker marker)
    {
        // Only a line that is exactly the marker word is rewritten, so an ordinary sentence that
        // merely begins with "thinking" or "response" is never mistaken for a reasoning marker.
        string token = MarkerText(marker);
        return trimmed.Value.Length == token.Length
            && trimmed.Value.Equals(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string MarkerText(Marker marker) => marker == Marker.Thinking ? "thinking" : "response";

    private static ReplyText StripTrailingFence(ReplyText content)
    {
        // Drop any line that is only a code-fence marker (``` or ```lang), so a final object wrapped in
        // a fenced block is seen on its own line without losing the object between the fences.
        string[] lines = content.Value.Split('\n');
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

        return new ReplyText(string.Join('\n', kept).Trim());
    }

    private static ReplyText? LastNonBlankLine(ReplyText content)
    {
        string[] lines = content.Value.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim().TrimEnd('\r').Trim();
            if (line.Length > 0)
            {
                return new ReplyText(line);
            }
        }

        return null;
    }

    private static bool TryWholeObject(ReplyText candidate, out ReplyText? result)
    {
        result = null;
        string trimmed = candidate.Value.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        if (!trimmed.EndsWith('}'))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                result = new ReplyText(trimmed);
                return true;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return false;
    }

    /// <summary>
    /// Scans for the last balanced top-level JSON object and returns it when it parses as an
    /// object. String literals and escapes are tracked so a brace inside a quoted reasoning
    /// snippet cannot unbalance the scan. An opening brace with no matching close is skipped and
    /// the scan continues, so a stray <c>{</c> in the reasoning cannot shadow a later valid object.
    /// In <see cref="ExtractionMode.RequireEndOfReply"/> mode, only an object whose closing brace
    /// reaches the end of the trimmed content is accepted (the judge's strict contract), and an
    /// object nested as the value of an unclosed wrapper (a reply truncated inside an outer object)
    /// is not taken as the final top-level verdict.
    /// </summary>
    private static FoundObject? LastBalancedObject(ReplyText content, ExtractionMode mode)
    {
        FoundObject? last = null;
        var bounds = new ScanBounds(mode, EndOfReply(content, mode));
        int i = 0;
        int lastUnclosedBrace = -1;
        while (i < content.Value.Length)
        {
            i = ScanAt(content, new ScanPosition(i), bounds, ref last, ref lastUnclosedBrace);
        }

        return last;
    }

    private static int EndOfReply(ReplyText content, ExtractionMode mode)
        => mode == ExtractionMode.RequireEndOfReply ? content.Value.TrimEnd().Length : -1;

    private static int ScanAt(ReplyText content, ScanPosition position, ScanBounds bounds, ref FoundObject? last, ref int lastUnclosedBrace)
    {
        if (content.Value[position.Value] != '{')
        {
            return position.Value + 1;
        }

        int end = FindObjectEnd(content, position);
        if (end < 0)
        {
            lastUnclosedBrace = position.Value;
            return position.Value + 1;
        }

        if (IsReasoningObject(bounds, end))
        {
            return end;
        }

        if (bounds.Mode == ExtractionMode.RequireEndOfReply && IsValueOfUnclosedWrapper(content, position.Value, lastUnclosedBrace))
        {
            // A verdict nested inside an unclosed outer object is a truncated wrapper, not the final
            // top-level verdict: e.g. {"draft":{"verdict":"pass",...} must not parse to the inner object.
            return end;
        }

        if (TryWholeObject(new ReplyText(content.Value[position.Value..end]), out ReplyText? parsed))
        {
            last = new FoundObject(parsed!.Value, end);
        }

        return end;
    }

    /// <summary>
    /// True when the object starting at <paramref name="objectStart"/> is the value of a property of
    /// an enclosing object whose opening brace was never closed — i.e. the reply is truncated inside
    /// a wrapper. The object is then nested, not the final top-level object.
    /// </summary>
    private static bool IsValueOfUnclosedWrapper(ReplyText content, int objectStart, int lastUnclosedBrace)
    {
        if (lastUnclosedBrace < 0 || objectStart <= lastUnclosedBrace)
        {
            return false;
        }

        int prev = objectStart - 1;
        while (prev >= 0 && char.IsWhiteSpace(content.Value[prev]))
        {
            prev--;
        }

        return prev >= 0 && content.Value[prev] == ':';
    }

    private static bool IsReasoningObject(ScanBounds bounds, int end)
    {
        if (bounds.Mode != ExtractionMode.RequireEndOfReply)
        {
            return false;
        }

        return end < bounds.EndOfReply;
    }

    private static int FindObjectEnd(ReplyText content, ScanPosition start)
    {
        var state = new ScanState(0, false, false);
        for (int i = start.Value; i < content.Value.Length; i++)
        {
            (state, bool closed) = AdvanceScan(state, content.Value[i]);
            if (closed)
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static (ScanState State, bool Closed) AdvanceScan(ScanState state, char c)
    {
        bool closed = false;
        if (state.InString)
        {
            state = AdvanceString(state, c);
        }
        else if (c == '"')
        {
            state = state with { InString = true };
        }
        else if (c == '{')
        {
            state = state with { Depth = state.Depth + 1 };
        }
        else if (c == '}')
        {
            state = state with { Depth = state.Depth - 1 };
            closed = state.Depth == 0;
        }

        return (state, closed);
    }

    private static ScanState AdvanceString(ScanState state, char c)
    {
        if (state.Escaped)
        {
            return state with { Escaped = false };
        }

        if (c == '\\')
        {
            return state with { Escaped = true };
        }

        if (c == '"')
        {
            return state with { InString = false };
        }

        return state;
    }

    private enum ExtractionMode
    {
        Permissive,
        RequireEndOfReply,
    }

    private enum Marker
    {
        Thinking,
        Response,
    }

    private enum ThinkStatus
    {
        Complete,
        Unterminated,
    }

    private readonly record struct ReplyText(string Value)
    {
        public ReplyText Trimmed() => new(Value.Trim());
    }

    private readonly record struct FoundObject(ReplyText Object, int End);

    private readonly record struct ThinkStripResult(ReplyText Content, ThinkStatus Status);

    private readonly record struct ScanPosition(int Value);

    private readonly record struct ScanBounds(ExtractionMode Mode, int EndOfReply);

    private readonly record struct ScanState(int Depth, bool InString, bool Escaped);
}
