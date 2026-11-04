import { useEffect, useRef, useState } from "react";
import type { PipelineReport } from "./api";
import type { BusyState } from "./hooks";

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

export function PipelinePanel({ busy, pipeline, resetGeneration, onRun, onTranscribe, onAudioChange }: {
  busy: BusyState;
  pipeline: PipelineReport | null;
  resetGeneration: number;
  onRun: (audio: File | null, flags: { scene: boolean; audition: boolean }) => Promise<void>;
  onTranscribe: (audio: File) => Promise<void>;
  onAudioChange: () => void;
}) {
  const [audio, setAudio] = useState<File | null>(null);
  const [sceneOn, setSceneOn] = useState(true);
  const [auditionOn, setAuditionOn] = useState(false);
  const audioInputRef = useRef<HTMLInputElement>(null);

  const [seenGeneration, setSeenGeneration] = useState(resetGeneration);
  if (seenGeneration !== resetGeneration) {
    setSeenGeneration(resetGeneration);
    setAudio(null);
  }

  useEffect(() => {
    if (audioInputRef.current) audioInputRef.current.value = "";
  }, [resetGeneration]);

  const chooseAudio = (next: File | null) => {
    setAudio(next);
    onAudioChange();
    if (audioInputRef.current) audioInputRef.current.value = "";
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
        <label className="pipeline-flag">
          <input type="checkbox" checked={auditionOn} disabled={busy !== null} onChange={(e) => setAuditionOn(e.target.checked)} />
          Audition
        </label>
        <button
          type="button"
          className="btn btn-primary"
          disabled={busy !== null}
          onClick={() => void onRun(audio, { scene: sceneOn, audition: auditionOn })}
        >
          {busy === "pipeline" ? "Running Pipeline…" : "Run Pipeline"}
        </button>
        <span className="toolbar-hint">
          {audio ? "transcribe → translation + QA" : "translation + QA"} · scene {sceneOn ? "on" : "off"} · audition {auditionOn ? "on" : "off"}
        </span>
      </div>
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
