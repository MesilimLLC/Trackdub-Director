import {
  type AuditionReport,
  type AuditionSegmentReport,
  type AuditionTake,
  type SegmentInput,
} from "./api";
import { fmtSec, verdictClass } from "./format";

export function AuditionAudioControls({ take }: { take: AuditionTake }) {
  const audioUrl = take.audio_url ?? take.audio_data_url;
  if (audioUrl) {
    return (
      <span className="audio-controls">
        <audio controls preload="metadata" src={audioUrl} aria-label={`Audition take ${take.take_number} audio`} />
        <a className="btn small" href={audioUrl} download={take.audio_file_name ?? `take-${take.take_number}.wav`}>Download WAV</a>
      </span>
    );
  }
  if (take.audio_transport_omitted) {
    return <span className="token-usage">Audio omitted: {take.audio_transport_omitted}</span>;
  }
  return null;
}

function speedLabel(take: AuditionTake): string {
  if (take.speed_factor != null) return ` ×${take.speed_factor}`;
  return take.speed_requested != null && take.speed_requested !== 1 ? ` ×${take.speed_requested}` : "";
}

function measuredLabel(take: AuditionTake): string {
  return take.measured_seconds != null
    ? `${fmtSec(take.measured_seconds)} / ${fmtSec(take.window_seconds)}`
    : `— / ${fmtSec(take.window_seconds)}`;
}

function timingClass(take: AuditionTake): string {
  if (take.timing_verdict === "Pass") return verdictClass("Pass");
  return take.timing_verdict === "Fail" ? verdictClass("Fail") : "verdict verdict-idle";
}

export function AuditionTakeRow({ take }: { take: AuditionTake }) {
  return (
    <div className="attempt-row">
      <span>#{take.take_number}</span>
      <span>{take.kind}{speedLabel(take)}</span>
      <span className="attempt-text">{take.text}</span>
      <span>{measuredLabel(take)}</span>
      <span className={timingClass(take)}>
        {take.timing_verdict}
      </span>
      <span className={verdictClass(take.meaning_verdict)} title={take.meaning_explanation ?? ""}>
        {take.meaning_verdict}
      </span>
      <span>{take.is_winner ? "Winner" : ""}</span>
      <span>{take.elevenlabs_characters > 0 ? `${take.elevenlabs_characters}c` : "—"}</span>
      <span className="take-audio-cell">
        {take.emotion_tag && <span className="meta-pill">Tag: {take.emotion_tag}</span>}
        <AuditionAudioControls take={take} />
      </span>
    </div>
  );
}

export function AuditionBudgetNotice({ totals }: { totals: AuditionReport["totals"] }) {
  const stops = [
    totals.meta_cap_hit && "Meta call budget reached",
    totals.elevenlabs_cap_hit && "ElevenLabs character cap reached",
  ].filter(Boolean);
  if (stops.length === 0) {
    return null;
  }

  return <div className="token-usage" role="status">Audition stopped early: {stops.join("; ")}. Remaining takes were not evaluated or synthesized.</div>;
}

export function AuditionPanel({ seg, audition }: { seg: SegmentInput; audition: AuditionSegmentReport }) {
  return (
    <div className="attempts-panel" aria-label={`Audition for ${seg.id}`}>
      <div className="token-usage">
        Audition: {audition.cast ? `Cast — take #${audition.winner_take_number} (${audition.winner_file_name ?? "—"})` : "Uncast"} · Speaker:{" "}
        {audition.speaker_id ?? "—"} · Voice: {audition.cast_voice ? `${audition.cast_voice.voice_name} (${audition.cast_voice.voice_id})` : "—"}
      </div>
      {audition.note && <div className="token-usage">{audition.note}</div>}
      <div className="attempt-row attempt-row-header">
        <span>#</span>
        <span>Kind</span>
        <span>Take</span>
        <span>Measured / window</span>
        <span>Timing</span>
        <span>Meaning</span>
        <span>Winner</span>
        <span>EL chars</span>
        <span>Audio</span>
      </div>
      {audition.takes.map((t) => (
        <AuditionTakeRow key={`${audition.id}-${t.take_number}`} take={t} />
      ))}
      {audition.takes
        .filter((t) => t.synthesis_note)
        .map((t) => (
          <div key={`${audition.id}-${t.take_number}-note`} className="token-usage">
            #{t.take_number}: {t.synthesis_note}
          </div>
        ))}
      {audition.takes
        .filter((t) => t.meaning_issues && t.meaning_issues.length > 0)
        .map((t) => (
          <div key={`${audition.id}-${t.take_number}-issues`} className="meaning-issues">
            <strong>#{t.take_number} meaning issues:</strong> {t.meaning_issues!.join("; ")}
          </div>
        ))}
    </div>
  );
}
