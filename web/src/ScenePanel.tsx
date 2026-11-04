import {
  type DirectorContext,
  type SceneRegion,
  type SceneSegmentResult,
  type SegmentInput,
  type VisionProvenance,
  type VisionUsage,
} from "./api";
import { listOrDash, orDash } from "./format";

export function SceneStatusPill({ scene }: { scene: SceneSegmentResult }) {
  const statusClass = scene.status === "Parsed" ? "verdict-pass" : scene.status === "Failed" ? "verdict-fail" : "verdict-idle";
  return (
    <span
      className={`verdict ${statusClass}`}
      title={`Scene context (${scene.provenance.model_id}, ${scene.provenance.prompt_template_version})`}
    >
      Scene: {scene.status}
    </span>
  );
}

export function RegionRow({ region, index }: { region: SceneRegion; index: number }) {
  return (
    <div key={`region-${index}`} className="token-usage">
      Region: {region.concept} ({region.x1},{region.y1})-({region.x2},{region.y2}) in {region.frame_width}x{region.frame_height}
      {region.mask_data ? " · mask present" : ""}
    </div>
  );
}

export function SceneHeader({ context }: { context: DirectorContext | undefined }) {
  const fields: Partial<DirectorContext> = context ?? {};
  return (
    <>
      <div className="token-usage">
        Speaker: {orDash(fields.speaker_label ?? fields.speaker_id)} · Emotion/tone: {orDash(fields.emotion_tone_label)}
      </div>
      <div className="token-usage">Speaker description: {orDash(fields.speaker_description)}</div>
      <div className="token-usage">On-screen text: {listOrDash(fields.on_screen_text)}</div>
      <div className="token-usage">Burned-in text: {listOrDash(fields.burned_in_text)}</div>
      <div className="token-usage">Scene summary: {orDash(fields.scene_summary)}</div>
    </>
  );
}

export function SceneRegions({ context }: { context: DirectorContext | undefined }) {
  const regions = context?.regions;
  if (!regions) {
    return null;
  }

  if (regions.length === 0) {
    return <div className="token-usage">Regions: none found</div>;
  }

  return (
    <>
      {regions.map((r, i) => (
        <RegionRow key={`region-${i}`} region={r} index={i} />
      ))}
    </>
  );
}

export function SceneDetails({ scene }: { scene: SceneSegmentResult }) {
  return (
    <>
      <SceneHeader context={scene.context} />
      <SceneRegions context={scene.context} />
    </>
  );
}

export function hasFrameInfo(scene: SceneSegmentResult): boolean {
  return Boolean(scene.frame_id || scene.timecode_seconds != null || scene.frame_sha256);
}

export function VisionGateLine({ provenance, usage }: { provenance: VisionProvenance; usage: VisionUsage | undefined }) {
  const reasoning = usage?.reasoning_tokens;
  return (
    <div className="token-usage">
      Vision gate: requested {provenance.requested_model_id} · returned {provenance.model_id} · template {provenance.prompt_template_version} · {provenance.timestamp_utc} · tokens{" "}
      {usage?.total_tokens ?? "—"}
      {reasoning != null ? ` (reasoning ${reasoning})` : ""}
    </div>
  );
}

export function FrameLine({ scene }: { scene: SceneSegmentResult }) {
  return (
    <div className="token-usage" title={scene.frame_sha256 ?? ""}>
      Frame: {orDash(scene.frame_id)} · {scene.timecode_seconds != null ? `${scene.timecode_seconds}s` : "—"} · sha256{" "}
      {scene.frame_sha256 ? `${scene.frame_sha256.slice(0, 12)}…` : "—"}
    </div>
  );
}

export function SceneVisionFooter({ scene }: { scene: SceneSegmentResult }) {
  const provenance = scene.vision_provenance;
  const hasFrame = hasFrameInfo(scene);
  if (!provenance && !hasFrame) {
    return null;
  }

  return (
    <>
      {provenance && <VisionGateLine provenance={provenance} usage={scene.vision_usage} />}
      {hasFrame && <FrameLine scene={scene} />}
    </>
  );
}

export function ScenePanel({ seg, scene }: { seg: SegmentInput; scene: SceneSegmentResult }) {
  return (
    <div className="attempts-panel" aria-label={`Scene context for ${seg.id}`}>
      {scene.status === "Failed" ? (
        <div className="meaning-issues">
          <strong>Scene failed:</strong> {scene.error ?? "unknown error"}
        </div>
      ) : (
        <SceneDetails scene={scene} />
      )}
      <div className="token-usage">
        Scene gate: model {scene.provenance.model_id} · template {scene.provenance.prompt_template_version} · {scene.provenance.timestamp_utc} · tokens{" "}
        {scene.usage.total_tokens} · images {scene.images_processed}
      </div>
      <SceneVisionFooter scene={scene} />
    </div>
  );
}
