using System.Globalization;
using System.Net;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Renders a <see cref="VerifyReceipt"/> as one standalone HTML page: inline CSS, no scripts, no external
/// assets. It reads only the receipt, so a page can be regenerated from the receipt JSON alone.
/// </summary>
public static class VerifyReceiptHtml
{
    private const string Css = """
        :root{color-scheme:light dark;--bg:#f6f7f9;--fg:#15181d;--card:#fff;--line:#d5d9e0;--muted:#5b6472;--ok:#0b7a3b;--ok-bg:#e3f4ea;--bad:#a4261a;--bad-bg:#fbe7e4}
        @media(prefers-color-scheme:dark){:root{--bg:#101318;--fg:#e8ebf0;--card:#181c23;--line:#2c333f;--muted:#99a3b3;--ok:#5fd18b;--ok-bg:#12301f;--bad:#ff8d7e;--bad-bg:#3a1a16}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:960px;margin:0 auto;padding:24px 16px 48px}
        h1{font-size:1.4rem;margin:0 0 4px}h2{font-size:1.05rem;margin:28px 0 10px}
        .muted{color:var(--muted)}.mono{font-family:ui-monospace,Consolas,monospace;font-size:.85em;word-break:break-all}
        .banner{border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:16px 0;display:flex;gap:16px;flex-wrap:wrap;align-items:center}
        .banner.ok{background:var(--ok-bg);border-color:var(--ok)}.banner.bad{background:var(--bad-bg);border-color:var(--bad)}
        .verdict{font-size:1.25rem;font-weight:700}.ok .verdict,.pill.ok{color:var(--ok)}.bad .verdict,.pill.bad{color:var(--bad)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:12px 0}
        .card header{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap;align-items:baseline}
        .pill{font-weight:600;font-size:.85rem}
        dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 14px;margin:10px 0 0}dt{color:var(--muted)}dd{margin:0}
        ul{margin:6px 0 0;padding-left:20px}
        table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600}
        """;

    public static string Render(VerifyReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        bool ok = receipt.Verdict == SegmentVerdict.Verified;
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        html.Append($"<title>Verify-only receipt: {E(receipt.RunFolder)}</title>\n<style>\n{Css}</style>\n</head>\n<body>\n<main>\n");
        html.Append($"<h1>Verify-only receipt</h1>\n<p class=\"muted\">Run folder <span class=\"mono\">{E(receipt.RunFolder)}</span> · schema <span class=\"mono\">{E(receipt.Schema)}</span> · tolerance {Num(receipt.ToleranceFraction * 100, "0.##")}% · media folder <span class=\"mono\">{E(receipt.MediaFolder)}</span></p>\n");
        html.Append($"<div class=\"banner {(ok ? "ok" : "bad")}\"><span class=\"verdict\">{E(receipt.Verdict.ToString())}</span>");
        html.Append($"<span>Exit status <strong>{receipt.ExitStatus}</strong></span>");
        html.Append($"<span>{receipt.SegmentsVerified} of {receipt.SegmentsTotal} lines verified</span></div>\n");
        html.Append($"<p class=\"muted\">{E(receipt.Seal)}</p>\n");
        AppendVoices(html, receipt);
        AppendInputs(html, receipt);
        html.Append("<h2>Lines</h2>\n");
        foreach (SegmentReceipt segment in receipt.Segments)
        {
            AppendSegment(html, segment);
        }

        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendVoices(StringBuilder html, VerifyReceipt receipt)
    {
        html.Append("<h2>Voices</h2>\n<table><thead><tr><th>Speaker</th><th>Voice</th><th>Voice id</th></tr></thead><tbody>\n");
        foreach (AuditionCastVoice voice in receipt.Voices)
        {
            html.Append($"<tr><td>{E(voice.SpeakerId)}</td><td>{E(voice.VoiceName)}</td><td class=\"mono\">{E(voice.VoiceId)}</td></tr>\n");
        }

        html.Append("</tbody></table>\n");
    }

    private static void AppendInputs(StringBuilder html, VerifyReceipt receipt)
    {
        html.Append("<h2>Input hashes</h2>\n<table><thead><tr><th>Artifact</th><th>SHA-256</th><th>How obtained</th></tr></thead><tbody>\n");
        foreach (InputHash input in receipt.Inputs)
        {
            html.Append($"<tr><td class=\"mono\">{E(input.Path)}</td><td class=\"mono\">{E(input.Sha256)}</td><td>{E(input.Label)}</td></tr>\n");
        }

        html.Append("</tbody></table>\n");
    }

    private static void AppendSegment(StringBuilder html, SegmentReceipt segment)
    {
        bool ok = segment.Verdict == SegmentVerdict.Verified;
        html.Append($"<section class=\"card\">\n<header><strong class=\"mono\">{E(segment.Id)}</strong><span class=\"pill {(ok ? "ok" : "bad")}\">{E(segment.Verdict.ToString())}");
        html.Append(segment.Reasons.Length == 0 ? string.Empty : $" · {E(string.Join(", ", segment.Reasons))}");
        html.Append("</span></header>\n<dl>\n");
        html.Append($"<dt>Source line</dt><dd>{E(segment.SourceLine)}</dd>\n");
        html.Append($"<dt>Spanish</dt><dd>{(segment.Spanish is null ? "<span class=\"muted\">none (line is uncast)</span>" : E(segment.Spanish))}</dd>\n");
        html.Append($"<dt>Winning voice</dt><dd>{(segment.WinningVoice is null ? "<span class=\"muted\">none</span>" : $"{E(segment.WinningVoice.VoiceName)} <span class=\"mono\">({E(segment.WinningVoice.SpeakerId)})</span>")}</dd>\n");
        html.Append($"<dt>Measured vs window</dt><dd>{TimingText(segment)}</dd>\n");
        html.Append($"<dt>Meaning</dt><dd>{MeaningText(segment.Meaning)}</dd>\n");
        html.Append($"<dt>Media</dt><dd>{MediaText(segment.Media)}</dd>\n");
        html.Append("</dl>\n");
        AppendRefusals(html, segment.Refusals);
        html.Append("</section>\n");
    }

    private static string TimingText(SegmentReceipt segment)
    {
        if (segment.Timing is not { } timing)
        {
            return $"<span class=\"muted\">not checked: no winning take; window {Num(segment.WindowSeconds, "0.####")} s</span>";
        }

        string measured = timing.MeasuredSeconds is { } m ? $"{Num(m, "0.0000")} s" : "no measurement";
        return $"{measured} against window {Num(timing.WindowSeconds, "0.####")} s (allowed up to {Num(timing.AllowedSeconds, "0.0000")} s): <strong>{E(timing.Verdict.ToString())}</strong>";
    }

    private static string MeaningText(MeaningCheck? meaning)
    {
        if (meaning is null)
        {
            return "<span class=\"muted\">not checked: no winning take</span>";
        }

        string reason = string.IsNullOrWhiteSpace(meaning.Reason) ? "no reason recorded" : E(meaning.Reason);
        return $"<strong>{E(meaning.Verdict.ToString())}</strong>: {reason}";
    }

    private static string MediaText(MediaCheck? media)
    {
        if (media is null)
        {
            return "<span class=\"muted\">not checked: no winning take</span>";
        }

        string name = media.FileName is null ? "no file name recorded" : $"<span class=\"mono\">{E(media.FileName)}</span>";
        string presence = media.Present ? "present on disk" : "<strong>not present on disk</strong>";
        string hash = media.Sha256 is null
            ? $"no hash ({E(media.HashLabel)})"
            : $"<span class=\"mono\">{E(media.Sha256)}</span> ({E(media.HashLabel)})";
        return $"{name}, {presence}; SHA-256 {hash}";
    }

    private static void AppendRefusals(StringBuilder html, RefusedTake[] refusals)
    {
        if (refusals.Length == 0)
        {
            return;
        }

        html.Append("<p class=\"muted\">Other takes</p>\n<ul>\n");
        foreach (RefusedTake take in refusals)
        {
            string reasons = take.Reasons.Length == 0 ? "no refusal reason" : string.Join(", ", take.Reasons);
            html.Append($"<li>Take {take.TakeNumber} ({E(take.Kind.ToString())}): {E(take.Text)} <strong>[{E(reasons)}]</strong> {E(take.Detail)}");
            html.Append(string.IsNullOrWhiteSpace(take.MeaningReason) ? string.Empty : $" Meaning gate: {E(take.MeaningReason)}");
            html.Append("</li>\n");
        }

        html.Append("</ul>\n");
    }

    private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
