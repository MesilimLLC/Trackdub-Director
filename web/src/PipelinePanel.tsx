import { useEffect, useRef, useState } from "react";
import type { PipelineReport, SegmentInput, SpeakerMapping } from "./api";
import type { BusyState } from "./hooks";
import { clearPendingIdempotencyKey, createIdempotencyKey, readPendingIdempotencyKey, savePendingIdempotencyKey } from "./idempotency";

export function PipelineStagePill({ stage }: { stage: PipelineReport["stages"][number] }) {
  const cls =
    stage.status === "Ran" ? "verdict-pass" : stage.status === "Failed" ? "verdict-fail" : "verdict-idle";
  const detail = stage.error ?? stage.note ?? "";
  return (
    <span className={`verdict ${cls}`} title={detail}>
      {stage.stage}: {stage.status}
      {detail ? `: ${detail}` : ""}
    </span>
  );
}

export function PipelinePanel({ busy, pipeline, resetGeneration, segments, speakerMapping, onSpeakerMappingChange, onRun, onTranscribe, onAudioChange, onRecoveryError }: {
  busy: BusyState;
  pipeline: PipelineReport | null;
  resetGeneration: number;
  segments: SegmentInput[];
  speakerMapping: SpeakerMapping;
  onSpeakerMappingChange: (mapping: SpeakerMapping) => void;
  onRun: (audio: File | null, flags: { scene: boolean; audition: boolean }, mapping: SpeakerMapping, idempotencyKey: string) => Promise<void>;
  onTranscribe: (audio: File) => Promise<void>;
  onAudioChange: () => void;
  onRecoveryError: (message: string) => void;
}) {
  const [audio, setAudio] = useState<File | null>(null);
  const [sceneOn, setSceneOn] = useState(true);
  const [auditionOn, setAuditionOn] = useState(false);
  const [retryKey, setRetryKey] = useState<string | null>(() => readPendingIdempotencyKey("pipeline"));
  const audioInputRef = useRef<HTMLInputElement>(null);
  const speakerLabels = [...new Set(["A", "B", ...segments.map((segment) => segment.speaker_id).filter((speaker): speaker is string => Boolean(speaker?.trim()))])];

  useEffect(() => {
    if (audioInputRef.current) audioInputRef.current.value = "";
  }, [resetGeneration]);

  useEffect(() => {
    if (pipeline && pipeline.stages.every((stage) => stage.status !== "Failed")) {
      clearPendingIdempotencyKey("pipeline");
      setRetryKey(null);
    }
  }, [pipeline]);

  const chooseAudio = (next: File | null) => {
    setAudio(next);
    onAudioChange();
    if (audioInputRef.current) audioInputRef.current.value = "";
  };

  const runWithKey = (key: string) => {
    if (!savePendingIdempotencyKey("pipeline", key)) {
      onRecoveryError("Recovery storage is unavailable, so the paid Pipeline request was not sent.");
      return;
    }
    setRetryKey(key);
    void onRun(audio, { scene: sceneOn, audition: auditionOn }, speakerMapping, key);
  };

  return (
    <section className="pipeline-panel" aria-label="Pipeline">
      <div className="pipeline-controls">
        <label className="btn file-btn">
          {audio ? audio.name : "Choose WAV"}
          <input
            ref={audioInputRef}
            type="file"
            accept=".wav,audio/wav,audio/x-wav"
            hidden
            onChange={(e) => chooseAudio(e.target.files?.[0] ?? null)}
          />
        </label>
        {audio && (
          <button type="button" className="btn" disabled={busy !== null} onClick={() => chooseAudio(null)}>
            Clear WAV
          </button>
        )}
        <button type="button" className="btn" disabled={busy !== null || !audio}
          onClick={() => audio && void onTranscribe(audio)}>
          {busy === "transcribe" ? "Transcribing…" : "Transcribe WAV"}
        </button>
        <label className="pipeline-flag">
          <input type="checkbox" checked={sceneOn} disabled={busy !== null} onChange={(e) => setSceneOn(e.target.checked)} />
          Scene
        </label>
        <div className="speaker-mapping" aria-label="Transcription speaker mapping">
          <span>ASR voices</span>
          {speakerLabels.map((speaker) => (
            <label key={speaker}>
              {speaker} →
              <select aria-label={`Map speaker ${speaker}`} value={speakerMapping[speaker] ?? ""} disabled={busy !== null}
                onChange={(event) => onSpeakerMappingChange({ ...speakerMapping, [speaker]: event.target.value as SpeakerMapping[string] })}>
                <option value="">Unmapped</option>
                <option value="anna">Anna</option>
                <option value="ashley">Ashley</option>
              </select>
            </label>
          ))}
        </div>
        <label className="pipeline-flag">
          <input type="checkbox" checked={auditionOn} disabled={busy !== null} onChange={(e) => setAuditionOn(e.target.checked)} />
          Audition
        </label>
        <button
          type="button"
          className="btn btn-primary"
          disabled={busy !== null}
          onClick={() => runWithKey(retryKey ?? createIdempotencyKey())}
        >
          {busy === "pipeline" ? "Running Pipeline…" : retryKey ? "Retry / retrieve same Pipeline run" : "Run Pipeline"}
        </button>
        {retryKey && <button type="button" className="btn" disabled={busy !== null}
          onClick={() => runWithKey(createIdempotencyKey())}>Start a new run</button>}
        <span className="toolbar-hint">
          {audio ? "transcribe → translation + QA" : "translation + QA"} · scene {sceneOn ? "on" : "off"} · audition {auditionOn ? "on" : "off"}
        </span>
      </div>
      {retryKey && <div className="token-usage">The same key is retained for 20 minutes. After a server restart, its provider outcome may be unknown; retrying that key will not rerun it. Starting a new run can repeat paid stages.</div>}
      {pipeline && (
        <div className="line-meta" aria-label="Pipeline stage status">
          {pipeline.stages.map((s) => (
            <PipelineStagePill key={s.stage} stage={s} />
          ))}
        </div>
      )}
    </section>
  );
}
