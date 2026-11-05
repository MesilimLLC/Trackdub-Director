import type { SegmentInput } from "./api";
import { FrameUploadField } from "./FrameUploadField";
import { VisualBoxEditor } from "./VisualBoxEditor";

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
      <FrameUploadField segment={segment} busy={busy} onFrameLoaded={onFrameLoaded} onError={onError} />
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
  if (!segment) return null;
  const update = (patch: Partial<SegmentInput>) => onChange({ ...segment, ...patch });
  return (
    <section className="workbench-panel" aria-label="Segment media editor">
      <div className="panel-heading"><h2>Scene, Director, and Visual — {segment.id || "selected line"}</h2></div>
      <SegmentMediaFields segment={segment} busy={busy} update={update} onFrameLoaded={onFrameLoaded} onError={onError} />
      <VisualBoxEditor segment={segment} onBox={(box) => update({ box })} onError={onError} />
    </section>
  );
}
