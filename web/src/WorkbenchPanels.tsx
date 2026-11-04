import { useRef, useState } from "react";
import type { AuditionReport, CopilotProposal, LocalisationBox, SegmentInput } from "./api";
import {
  type ChangeKind,
  type PointerPoint,
  type RunSettings,
  MAX_IMAGE_BYTES,
  buildDubExportPlacements,
  callEstimates,
  dubExportReadiness,
  nextSegmentId,
  normalizeBox,
  pointerToOriginal,
  validateImageMetadata,
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

async function sha256Hex(file: File): Promise<string | null> {
  if (!globalThis.crypto?.subtle) return null;
  const digest = await globalThis.crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function readFrameFile(file: File): Promise<{ dataUrl: string; width: number; height: number; sha256: string | null }> {
  const objectUrl = URL.createObjectURL(file);
  try {
    const dims = await new Promise<{ width: number; height: number }>((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve({ width: image.naturalWidth, height: image.naturalHeight });
      image.onerror = () => reject(new Error("Could not read frame image."));
      image.src = objectUrl;
    });
    const error = validateImageMetadata(file, dims.width, dims.height);
    if (error) throw new Error(error);
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(new Error("Could not read frame image."));
      reader.readAsDataURL(file);
    });
    return { dataUrl, ...dims, sha256: await sha256Hex(file) };
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

function SegmentMediaFields({
  segment,
  busy,
  update,
  onFrameLoaded,
  onError,
}: {
  segment: SegmentInput;
  busy: boolean;
  update: (patch: Partial<SegmentInput>) => void;
  onFrameLoaded: (expected: SegmentInput, patch: Partial<SegmentInput>) => boolean;
  onError: (message: string | null) => void;
}) {
  return (
    <div className="workbench-grid">
      <label>Frame image
        <input aria-label="Frame image" type="file" accept="image/png,image/jpeg,image/webp" disabled={busy} onChange={(e) => {
          const file = e.target.files?.[0];
          e.target.value = "";
          if (!file) return;
          if (file.size > MAX_IMAGE_BYTES) { onError("Frame exceeds the 10 MiB limit."); return; }
          const expected = segment;
          void readFrameFile(file).then(({ dataUrl, sha256 }) => {
            const applied = onFrameLoaded(expected, {
              frame_image_data_url: dataUrl,
              frame_id: expected.frame_id ?? file.name,
              frame_sha256: sha256,
            });
            onError(applied ? null : "Frame upload finished after that segment was replaced. Choose the frame again.");
          }).catch((error: unknown) => onError(error instanceof Error ? error.message : String(error)));
        }} />
      </label>
      <label>Scene concepts (comma separated)
        <input aria-label="Scene concepts" value={(segment.concepts ?? []).join(", ")} disabled={busy}
          onChange={(e) => update({ concepts: e.target.value.split(",").map((v) => v.trim()).filter(Boolean) })} />
      </label>
      <label>Speaker label
        <input aria-label="Speaker label" value={segment.speaker_label ?? ""} disabled={busy}
          onChange={(e) => update({ speaker_label: e.target.value || null })} />
      </label>
      <label>Frame timecode seconds
        <input aria-label="Frame timecode seconds" type="number" min="0" step="0.01" value={segment.timecode_seconds ?? ""} disabled={busy}
          onChange={(e) => update({ timecode_seconds: e.target.value === "" ? null : Number(e.target.value) })} />
      </label>
      <label>Director register
        <select aria-label="Director register" value={segment.director_adaptation?.target_register ?? ""} disabled={busy}
          onChange={(e) => update({ director_adaptation: e.target.value ? { target_register: e.target.value } : null })}>
          <option value="">Not requested</option>
          <option value="formal">Formal</option>
          <option value="informal">Informal</option>
        </select>
      </label>
      <label className="check-field">
        <input type="checkbox" checked={segment.vision_enrichment ?? false} disabled={busy}
          onChange={(e) => update({ vision_enrichment: e.target.checked })} /> Vision enrichment (1 Meta call)
      </label>
      <label>Sign source text
        <input aria-label="Sign source text" value={segment.source_text ?? ""} disabled={busy}
          onChange={(e) => update({ source_text: e.target.value || null })} />
      </label>
      <label>Sign target text
        <input aria-label="Sign target text" value={segment.target_text ?? ""} disabled={busy}
          onChange={(e) => update({ target_text: e.target.value || null })} />
      </label>
    </div>
  );
}

function VisualBoxEditor({
  segment,
  natural,
  shownBox,
  frameRef,
  onNatural,
  onPointerDown,
  onPointerMove,
  onPointerUp,
}: {
  segment: SegmentInput;
  natural: { width: number; height: number } | null;
  shownBox: LocalisationBox | null | undefined;
  frameRef: React.RefObject<HTMLDivElement | null>;
  onNatural: (size: { width: number; height: number }) => void;
  onPointerDown: (event: React.PointerEvent) => void;
  onPointerMove: (event: React.PointerEvent) => void;
  onPointerUp: (event: React.PointerEvent) => void;
}) {
  if (!segment.frame_image_data_url) {
    return <div className="token-usage">Upload a frame to enable Scene visual context and the sign box editor.</div>;
  }
  return (
    <>
      <div
        ref={frameRef}
        className="visual-editor"
        role="application"
        aria-label="Draw sign box"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
      >
        <img src={segment.frame_image_data_url} alt={`Frame for ${segment.id}`} draggable={false}
          onLoad={(e) => onNatural({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })} />
        {shownBox && natural && (
          <div className="visual-box" style={{
            left: `${(shownBox.x1 / natural.width) * 100}%`,
            top: `${(shownBox.y1 / natural.height) * 100}%`,
            width: `${((shownBox.x2 - shownBox.x1) / natural.width) * 100}%`,
            height: `${((shownBox.y2 - shownBox.y1) / natural.height) * 100}%`,
          }} />
        )}
      </div>
      <div className="token-usage">
        Drag on the frame to draw the sign box. Coordinates use original pixels; x2/y2 are exclusive.
        {segment.box ? ` Current: (${segment.box.x1},${segment.box.y1})–(${segment.box.x2},${segment.box.y2}).` : " No box selected."}
      </div>
    </>
  );
}

export function SegmentMediaEditor({
  segment,
  busy,
  onChange,
  onFrameLoaded,
  onError,
}: {
  segment: SegmentInput | undefined;
  busy: boolean;
  onChange: (next: SegmentInput) => void;
  onFrameLoaded: (expected: SegmentInput, patch: Partial<SegmentInput>) => boolean;
  onError: (message: string | null) => void;
}) {
  const [natural, setNatural] = useState<{ width: number; height: number } | null>(null);
  const [dragStart, setDragStart] = useState<PointerPoint | null>(null);
  const [draftBox, setDraftBox] = useState<LocalisationBox | null>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  if (!segment) return null;

  const update = (patch: Partial<SegmentInput>) => onChange({ ...segment, ...patch });
  const pointFromEvent = (event: React.PointerEvent): PointerPoint | null => {
    const rect = frameRef.current?.getBoundingClientRect();
    if (!rect || !natural) return null;
    return pointerToOriginal(event.clientX, event.clientY, rect, natural);
  };
  const shownBox = draftBox ?? segment.box;

  return (
    <section className="workbench-panel" aria-label="Segment media editor">
      <div className="panel-heading"><h2>Scene, Director, and Visual — {segment.id || "selected line"}</h2></div>
      <SegmentMediaFields segment={segment} busy={busy} update={update} onFrameLoaded={onFrameLoaded} onError={onError} />
      <VisualBoxEditor
        segment={segment}
        natural={natural}
        shownBox={shownBox}
        frameRef={frameRef}
        onNatural={setNatural}
        onPointerDown={(e) => { const p = pointFromEvent(e); if (p) { setDragStart(p); setDraftBox(null); } }}
        onPointerMove={(e) => {
          if (!dragStart || !natural) return;
          const p = pointFromEvent(e);
          if (p) setDraftBox(normalizeBox(dragStart, p, natural));
        }}
        onPointerUp={(e) => {
          if (!dragStart || !natural) return;
          const p = pointFromEvent(e);
          const box = p ? normalizeBox(dragStart, p, natural) : null;
          setDragStart(null);
          setDraftBox(null);
          if (box) update({ box });
          else onError("Sign box must be at least 8×8 original pixels.");
        }}
      />
    </section>
  );
}
