import { useEffect, useRef, useState } from "react";
import {
  type PipelineReport,
} from "./api";
import { type BusyState } from "./hooks";

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

export function PipelinePanel({ busy, pipeline, resetGeneration, onRun }: {
  busy: BusyState;
  pipeline: PipelineReport | null;
  resetGeneration: number;
  onRun: (audio: File | null, flags: { scene: boolean; audition: boolean }) => Promise<void>;
}) {
  const [audio, setAudio] = useState<File | null>(null);
  const [sceneOn, setSceneOn] = useState(true);
  const [auditionOn, setAuditionOn] = useState(false);
  const audioInputRef = useRef<HTMLInputElement>(null);

  // Reset the chosen file when the parent bumps resetGeneration: adjust state
  // during render, and clear the uncontrolled input element in an effect.
  const [seenGeneration, setSeenGeneration] = useState(resetGeneration);
  if (seenGeneration !== resetGeneration) {
    setSeenGeneration(resetGeneration);
    setAudio(null);
  }

  useEffect(() => {
    if (audioInputRef.current) audioInputRef.current.value = "";
  }, [resetGeneration]);

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
            onChange={(e) => {
              setAudio(e.target.files?.[0] ?? null);
              if (audioInputRef.current) audioInputRef.current.value = "";
            }}
          />
        </label>
        {audio && (
          <button
            type="button"
            className="btn"
            onClick={() => {
              setAudio(null);
              if (audioInputRef.current) audioInputRef.current.value = "";
            }}
          >
            Clear WAV
          </button>
        )}
        <label className="pipeline-flag">
          <input type="checkbox" checked={sceneOn} onChange={(e) => setSceneOn(e.target.checked)} />
          Scene
        </label>
        <label className="pipeline-flag">
          <input type="checkbox" checked={auditionOn} onChange={(e) => setAuditionOn(e.target.checked)} />
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
