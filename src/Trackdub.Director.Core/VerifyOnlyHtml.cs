using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Renders a standalone HTML page from a verify-only receipt alone
/// (inline CSS, no external assets, no JavaScript dependency).
/// </summary>
public static class VerifyOnlyHtml
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RenderFromJson(string receiptJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptJson);
        VerifyOnlyReceipt? receipt = JsonSerializer.Deserialize<VerifyOnlyReceipt>(receiptJson, JsonOptions);
        if (receipt is null)
        {
            throw new InvalidOperationException("Invalid verify-only receipt JSON.");
        }

        return Render(receipt);
    }

    public static string Render(VerifyOnlyReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Verify-only receipt (hash-sealed)</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<main>");
        sb.AppendLine("<header class=\"top\">");
        sb.AppendLine("<p class=\"eyebrow\">Trackdub-Director verify-only</p>");
        sb.AppendLine("<h1>Hash-sealed receipt</h1>");
        sb.AppendLine("<p class=\"lede\">Recomputed from recorded numbers. Hash-sealed only (not a signature / certificate / attestation). A check that was not run never counts as a pass.</p>");
        sb.AppendLine("<dl class=\"summary\">");
        AppendDl(sb, "Overall verdict", receipt.OverallVerdict.ToString());
        AppendDl(sb, "Exit status", receipt.ExitStatus.ToString());
        AppendDl(sb, "Tolerance", receipt.ToleranceFraction.ToString("0.##%"));
        AppendDl(sb, "Run directory", receipt.RunDir);
        sb.AppendLine("</dl>");
        sb.AppendLine("</header>");

        sb.AppendLine("<section>");
        sb.AppendLine("<h2>Input hashes</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr><th>Path</th><th>SHA-256</th><th>Label</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (VerifyOnlyHashEntry hash in receipt.InputHashes)
        {
            sb.AppendLine("<tr>");
            sb.Append("<td>").Append(E(hash.Path)).AppendLine("</td>");
            sb.Append("<td class=\"mono\">").Append(E(hash.Sha256 ?? "(none)")).AppendLine("</td>");
            sb.Append("<td>").Append(E(ProvenanceLabel(hash.Provenance))).AppendLine("</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</section>");

        sb.AppendLine("<section>");
        sb.AppendLine("<h2>Segments</h2>");
        foreach (VerifyOnlySegmentReceipt seg in receipt.Segments)
        {
            string verdictClass = seg.Verdict == VerifyOnlySegmentVerdict.Verified ? "ok" : "review";
            sb.Append("<article class=\"segment ").Append(verdictClass).AppendLine("\">");
            sb.Append("<h3>").Append(E(seg.Id)).Append(" · ").Append(E(seg.Verdict.ToString())).AppendLine("</h3>");
            if (seg.Reasons.Length > 0)
            {
                sb.Append("<p class=\"reasons\">Reasons: ").Append(E(string.Join(", ", seg.Reasons))).AppendLine("</p>");
            }

            sb.AppendLine("<dl>");
            AppendDl(sb, "Source", seg.SourceText);
            AppendDl(sb, "Spanish", seg.SpanishText ?? "(none)");
            AppendDl(sb, "Measured vs window", FormatTiming(seg));
            AppendDl(sb, "Meaning verdict", FormatMeaning(seg));
            if (!string.IsNullOrWhiteSpace(seg.MeaningExplanation))
            {
                AppendDl(sb, "Meaning reason", seg.MeaningExplanation!);
            }

            AppendDl(sb, "Winning voice", FormatVoice(seg.WinningVoice));
            AppendDl(sb, "Winner file", seg.WinnerFileName ?? "(none)");
            AppendDl(sb, "Media present", seg.MediaPresent ? "yes" : "no");
            if (seg.WinningMediaHash is not null)
            {
                AppendDl(
                    sb,
                    "Winning media hash",
                    $"{seg.WinningMediaHash.Sha256 ?? "(none)"} ({ProvenanceLabel(seg.WinningMediaHash.Provenance)})");
            }

            sb.AppendLine("</dl>");

            if (seg.Refusals.Length > 0)
            {
                sb.AppendLine("<h4>Refusals</h4>");
                sb.AppendLine("<ul>");
                foreach (VerifyOnlyRefusal refusal in seg.Refusals)
                {
                    sb.Append("<li><strong>take ").Append(refusal.TakeNumber).Append("</strong>: ")
                        .Append(E(refusal.Text))
                        .Append(" · ")
                        .Append(E(string.Join(", ", refusal.Reasons)));
                    if (!string.IsNullOrWhiteSpace(refusal.MeaningExplanation))
                    {
                        sb.Append(" · ").Append(E(refusal.MeaningExplanation!));
                    }

                    sb.AppendLine("</li>");
                }

                sb.AppendLine("</ul>");
            }

            sb.AppendLine("</article>");
        }

        sb.AppendLine("</section>");
        sb.AppendLine("<footer><p>").Append(E(receipt.Notes)).AppendLine("</p></footer>");
        sb.AppendLine("</main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string FormatTiming(VerifyOnlySegmentReceipt seg)
    {
        string measured = seg.MeasuredSeconds?.ToString("0.####") ?? "(none)";
        string pass = seg.TimingRecomputedPass is null
            ? "not checked"
            : seg.TimingRecomputedPass.Value ? "pass" : "fail";
        return $"{measured}s vs {seg.WindowSeconds:0.####}s window ({pass})";
    }

    private static string FormatMeaning(VerifyOnlySegmentReceipt seg)
    {
        if (seg.MeaningVerdict is null)
        {
            return "(none)";
        }

        return seg.MeaningVerdict == MeaningVerdict.NotEvaluated
            ? "NotEvaluated (not a pass)"
            : seg.MeaningVerdict.Value.ToString();
    }

    private static string FormatVoice(AuditionCastVoice? voice)
    {
        if (voice is null)
        {
            return "(none)";
        }

        return $"{voice.VoiceName} ({voice.SpeakerId} / {voice.VoiceId})";
    }

    private static string ProvenanceLabel(VerifyOnlyHashProvenance provenance) => provenance switch
    {
        VerifyOnlyHashProvenance.Recomputed => "recomputed",
        VerifyOnlyHashProvenance.RecordedAtCapture => "recorded-at-capture",
        _ => provenance.ToString(),
    };

    private static void AppendDl(StringBuilder sb, string term, string value)
    {
        sb.Append("<div><dt>").Append(E(term)).Append("</dt><dd>").Append(E(value)).AppendLine("</dd></div>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private const string Css = """
        :root { color-scheme: light; --ink:#1c1917; --muted:#57534e; --line:#d6d3d1; --ok:#166534; --review:#9a3412; --bg:#fafaf9; --card:#fff; }
        * { box-sizing: border-box; }
        body { margin: 0; font: 16px/1.5 "Segoe UI", "Helvetica Neue", sans-serif; color: var(--ink); background: linear-gradient(180deg,#f5f5f4,var(--bg)); }
        main { max-width: 920px; margin: 0 auto; padding: 2rem 1.25rem 4rem; }
        h1,h2,h3,h4 { line-height: 1.2; margin: 0 0 0.6rem; }
        .eyebrow { text-transform: uppercase; letter-spacing: 0.08em; font-size: 0.75rem; color: var(--muted); margin: 0 0 0.4rem; }
        .lede { color: var(--muted); max-width: 42rem; }
        .summary { display: grid; grid-template-columns: 10rem 1fr; gap: 0.35rem 1rem; margin: 1.25rem 0 0; }
        .summary dt { color: var(--muted); }
        .summary dd { margin: 0; font-weight: 600; }
        section { margin-top: 2rem; }
        table { width: 100%; border-collapse: collapse; background: var(--card); }
        th, td { border: 1px solid var(--line); padding: 0.55rem 0.65rem; text-align: left; vertical-align: top; }
        th { background: #f5f5f4; font-size: 0.85rem; }
        .mono { font-family: ui-monospace, Consolas, monospace; font-size: 0.78rem; word-break: break-all; }
        .segment { background: var(--card); border: 1px solid var(--line); border-left-width: 4px; padding: 1rem 1.1rem; margin: 1rem 0; }
        .segment.ok { border-left-color: var(--ok); }
        .segment.review { border-left-color: var(--review); }
        .segment dl { display: grid; grid-template-columns: 11rem 1fr; gap: 0.35rem 1rem; margin: 0.75rem 0 0; }
        .segment dt { color: var(--muted); }
        .segment dd { margin: 0; }
        .reasons { color: var(--review); font-weight: 600; }
        ul { padding-left: 1.2rem; }
        footer { margin-top: 2.5rem; color: var(--muted); font-size: 0.9rem; }
        """;
}
