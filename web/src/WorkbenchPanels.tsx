import { useState } from "react";
import type { AuditionReport, CopilotProposal, SegmentInput } from "./api";
import {
  type ChangeKind,
  type RunSettings,
  buildDubExportPlacements,
  callEstimates,
  dubExportReadiness,
  nextSegmentId,
} from "./workbench";

export function CopilotPanel({
  goal,
  proposal,
  busy,
  disabled,
  onGoalChange,
  onPrepare,
  onApply,
  onDiscard,
}: {
  goal: string;
  proposal: CopilotProposal | null;
  busy: boolean;
  disabled: boolean;
  onGoalChange: (value: string) => void;
  onPrepare: () => void;
  onApply: () => void;
  onDiscard: () => void;
}) {
  return (
    <section className="workbench-panel" aria-label="Copilot">
      <div className="panel-heading">
        <h2>Copilot preparation</h2>
        <span className="toolbar-hint">Advisory only · Apply settings never runs a stage</span>
      </div>
      <label>Goal
        <textarea className="copilot-textarea" aria-label="Copilot goal" value={goal} disabled={busy}
          onChange={(e) => onGoalChange(e.target.value)} placeholder="Describe the dub, scene, register, or sign preparation you want" />
      </label>
      <div className="workbench-actions">
        <button type="button" className="btn btn-primary" disabled={busy || disabled || goal.trim() === ""} onClick={onPrepare}>
          {busy ? "Preparing…" : "Prepare with Copilot — 1 Meta call"}
        </button>
        {proposal && <button type="button" className="btn" onClick={onDiscard}>Discard proposal</button>}
      </div>
      {proposal && (
        <div className="copilot-proposal">
          <div><strong>Template:</strong> {proposal.template} · <strong>Model:</strong> {proposal.model}</div>
          <p>{proposal.explanation}</p>
          {proposal.missing_inputs.length > 0 && <div>Missing: {proposal.missing_inputs.join(" · ")}</div>}
          {proposal.warnings.length > 0 && <div>Warnings: {proposal.warnings.join(" · ")}</div>}
          <div className="token-usage">
            Proposed settings: {JSON.stringify(proposal.settings)} · Segment updates: {proposal.segment_updates.length}
          </div>
          <button type="button" className="btn btn-primary" disabled={busy} onClick={onApply}>Apply settings</button>
        </div>
      )}
    </section>
  );
}

export function RunSettingsPanel({
  from,
  to,
  settings,
  segments,
  busy,
  evidenceAvailable,
  onFromChange,
  onToChange,
  onSettingsChange,
  onDownloadInput,
  onDownloadEvidence,
}: {
  from: string;
  to: string;
  settings: RunSettings;
  segments: SegmentInput[];
  busy: boolean;
  evidenceAvailable: boolean;
  onFromChange: (value: string) => void;
  onToChange: (value: string) => void;
  onSettingsChange: (next: RunSettings, change: ChangeKind) => void;
  onDownloadInput: () => void;
  onDownloadEvidence: () => void;
}) {
  const estimates = callEstimates(segments, settings);
  const set = (patch: Partial<RunSettings>, change: ChangeKind) => onSettingsChange({ ...settings, ...patch }, change);
  return (
    <section className="workbench-panel" aria-label="Run settings">
      <div className="workbench-grid">
        <label>Source language
          <input aria-label="Source language" value={from} disabled={busy} onChange={(e) => onFromChange(e.target.value)} placeholder="en" />
        </label>
        <label>Target language
          <input aria-label="Target language" value={to} disabled={busy} onChange={(e) => onToChange(e.target.value)} placeholder="es" />
        </label>
        <label>Maximum revisions
          <input aria-label="Maximum revisions" type="number" min="0" step="1" value={settings.maxRevisions} disabled={busy}
            onChange={(e) => set({ maxRevisions: Number(e.target.value) }, "qaOptions")} />
          <small>Initial translation plus up to this many revisions per line.</small>
        </label>
        <label>Timing tolerance %
          <input aria-label="Timing tolerance percent" type="number" min="0" step="1" value={settings.tolerancePercent} disabled={busy}
            onChange={(e) => set({ tolerancePercent: Number(e.target.value) }, "qaOptions")} />
        </label>
        <label>Target CPS (optional)
          <input aria-label="Target characters per second" type="number" min="0" step="0.1" value={settings.targetCps} disabled={busy}
            onChange={(e) => set({ targetCps: e.target.value }, "qaOptions")} />
        </label>
        <label>Speed cap
          <input aria-label="Speed retake cap" type="number" min="1" max="1.2" step="0.01" value={settings.speedCap} disabled={busy || !settings.speedRetakeEnabled}
            onChange={(e) => set({ speedCap: Number(e.target.value) }, "auditionOptions")} />
        </label>
        <label className="check-field">
          <input type="checkbox" checked={settings.speedRetakeEnabled} disabled={busy}
            onChange={(e) => set({ speedRetakeEnabled: e.target.checked }, "auditionOptions")} /> Computed-speed retakes
        </label>
        <label className="check-field">
          <input type="checkbox" checked={settings.emotionTagsEnabled} disabled={busy}
            onChange={(e) => set({ emotionTagsEnabled: e.target.checked }, "auditionOptions")} /> Emotion/pacing audio tags
        </label>
      </div>
      <div className="estimate-line" aria-label="Call estimates">
        Estimates: Translate {estimates.translate} · QA {estimates.qa} · Scene {estimates.scene} · Director {estimates.director} · Audition {estimates.audition} · Visual {estimates.visual}
      </div>
      <div className="workbench-actions">
        <button type="button" className="btn" onClick={onDownloadInput}>Download input JSON</button>
        <button type="button" className="btn" onClick={onDownloadEvidence} disabled={!evidenceAvailable}>Download evidence JSON</button>
        <span className="toolbar-hint">Windows-hosted backend required for Visual image decoding (WIC).</span>
      </div>
    </section>
  );
}

export function SegmentEditor({
  segments,
  busy,
  selectedIndex,
  onSelect,
  onChange,
}: {
  segments: SegmentInput[];
  busy: boolean;
  selectedIndex: number;
  onSelect: (index: number) => void;
  onChange: (next: SegmentInput[]) => void;
}) {
  const update = (index: number, patch: Partial<SegmentInput>) => {
    onChange(segments.map((segment, i) => (i === index ? { ...segment, ...patch } : segment)));
  };
  return (
    <section className="workbench-panel" aria-label="Segment editor">
      <div className="panel-heading">
        <h2>Dialogue segments</h2>
        <button type="button" className="btn" disabled={busy} onClick={() => {
          const next = [...segments, { id: nextSegmentId(segments), text: "", start_seconds: null, duration_seconds: null, speaker_id: null }];
          onChange(next);
          onSelect(next.length - 1);
        }}>Add line</button>
      </div>
      <div className="segment-editor-head" aria-hidden="true"><span>ID</span><span>Source text</span><span>Start</span><span>Duration</span><span>Speaker</span><span /></div>
      {segments.map((segment, index) => (
        <div className={`segment-editor-row ${index === selectedIndex ? "selected" : ""}`} key={index}>
          <input aria-label={`Segment ID ${index + 1}`} value={segment.id} disabled={busy} onFocus={() => onSelect(index)}
            onChange={(e) => update(index, { id: e.target.value })} />
          <textarea aria-label={`Source text ${index + 1}`} value={segment.text} disabled={busy} rows={2} onFocus={() => onSelect(index)}
            onChange={(e) => update(index, { text: e.target.value })} />
          <input aria-label={`Start seconds ${index + 1}`} type="number" min="0" step="0.01" value={segment.start_seconds ?? ""} disabled={busy} onFocus={() => onSelect(index)}
            onChange={(e) => update(index, { start_seconds: e.target.value === "" ? null : Number(e.target.value) })} />
          <input aria-label={`Duration seconds ${index + 1}`} type="number" min="0" step="0.1" value={segment.duration_seconds ?? ""} disabled={busy} onFocus={() => onSelect(index)}
            onChange={(e) => update(index, { duration_seconds: e.target.value === "" ? null : Number(e.target.value) })} />
          <input aria-label={`Speaker ID ${index + 1}`} value={segment.speaker_id ?? ""} disabled={busy} onFocus={() => onSelect(index)}
            onChange={(e) => update(index, { speaker_id: e.target.value || null })} />
          <span className="row-actions">
            <button type="button" className="btn small" onClick={() => onSelect(index)}>Media</button>
            <button type="button" className="btn small" disabled={busy || segments.length === 0}
              onClick={() => onChange(segments.filter((_, i) => i !== index))}>Remove</button>
          </span>
        </div>
      ))}
      {segments.length === 0 && <div className="token-usage">No segments. Add a line, load JSON, sample, WAV, or transcription.</div>}
    </section>
  );
}

function DubExportControls({
  video,
  busy,
  exporting,
  ready,
  onVideo,
  onClear,
  onExport,
}: {
  video: File | null;
  busy: boolean;
  exporting: boolean;
  ready: boolean;
  onVideo: (file: File | null) => void;
  onClear: () => void;
  onExport: (video: File) => Promise<void>;
}) {
  return (
    <div className="workbench-actions">
      <label className="btn file-btn">
        {video ? video.name : "Choose original video"}
        <input
          aria-label="Original video"
          type="file"
          accept=".mp4,.mov,.m4v,.webm,.mkv,video/mp4,video/quicktime,video/webm,video/x-matroska"
          hidden
          disabled={busy}
          onChange={(e) => {
            onVideo(e.target.files?.[0] ?? null);
            e.target.value = "";
          }}
        />
      </label>
      {video && (
        <button type="button" className="btn" disabled={busy} onClick={onClear}>
          Clear video
        </button>
      )}
      <button
        type="button"
        className="btn btn-primary"
        disabled={busy || !ready}
        onClick={() => video && void onExport(video)}
      >
        {exporting ? "Exporting dubbed MP4…" : "Export dubbed MP4"}
      </button>
    </div>
  );
}

export function DubExportPanel({
  segments,
  audition,
  busy,
  exporting,
  resetGeneration,
  onExport,
}: {
  segments: SegmentInput[];
  audition: AuditionReport | null;
  busy: boolean;
  exporting: boolean;
  resetGeneration: number;
  onExport: (video: File) => Promise<void>;
}) {
  const [video, setVideo] = useState<File | null>(null);
  const [seenGeneration, setSeenGeneration] = useState(resetGeneration);
  if (seenGeneration !== resetGeneration) {
    setSeenGeneration(resetGeneration);
    setVideo(null);
  }

  const built = buildDubExportPlacements(segments, audition);
  const problems = [...built.problems];
  if (video && video.size > 200 * 1024 * 1024) problems.push("Original video exceeds the 200 MiB export limit.");
  const ready = Boolean(video && problems.length === 0);

  return (
    <section className="workbench-panel" aria-label="Dub export">
      <div className="panel-heading">
        <h2>Dub export</h2>
        <span className="toolbar-hint">Original audio muted · Winning takes only · MP4</span>
      </div>
      <DubExportControls
        video={video}
        busy={busy}
        exporting={exporting}
        ready={ready}
        onVideo={setVideo}
        onClear={() => setVideo(null)}
        onExport={onExport}
      />
      <div className="token-usage" aria-label="Dub export readiness">
        {dubExportReadiness(video, built, problems)}
      </div>
    </section>
  );
}
