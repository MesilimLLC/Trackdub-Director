import {
  type DirectorAdaptationInput,
  type QaSegmentReport,
  type SceneSegmentResult,
  type SegmentInput,
} from "./api";

// Prefer index when scene.segments length matches the working set (standalone
// lane, including duplicate ids). Otherwise resolve by id (pipeline returns
// only framed segments).
export function resolveScene(
  seg: SegmentInput,
  idx: number,
  { sceneById, sceneBySegId }: Pick<DirectorLookups, "sceneById" | "sceneBySegId">,
  segmentCount: number,
): SceneSegmentResult | undefined {
  const useSceneIndex = sceneById != null && sceneById.size === segmentCount;
  return useSceneIndex ? sceneById?.get(idx) : sceneBySegId?.get(seg.id);
}

export type SceneContext = NonNullable<SceneSegmentResult["context"]>;

export function toDirectorContext(c: SceneContext): NonNullable<DirectorAdaptationInput["context"]> {
  return {
    speaker_id: c.speaker_id ?? null,
    speaker_label: c.speaker_label ?? null,
    emotion_tone_label: c.emotion_tone_label ?? null,
    on_screen_text: c.on_screen_text ?? null,
    scene_summary: c.scene_summary ?? null,
    speaker_description: c.speaker_description ?? null,
    burned_in_text: c.burned_in_text ?? null,
  };
}

export interface DirectorLookups {
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  sceneBySegId: Map<string, SceneSegmentResult> | null;
}

// Existing translation: explicit input wins, else the QA final line, else the
// plain translation. Context: explicit input wins, else the scene lane's
// context for this segment. Segments without an opt-in are sent as-is and come
// back NotRequested with no calls made for them.
export function prepareDirectorInput(
  seg: SegmentInput,
  idx: number,
  segmentCount: number,
  lookups: DirectorLookups,
): DirectorAdaptationInput {
  const { qaById, batchById } = lookups;
  const sceneContext = resolveScene(seg, idx, lookups, segmentCount)?.context;
  return {
    id: seg.id,
    text: seg.text,
    translation: seg.translation ?? qaById?.get(seg.id)?.final_translation ?? batchById?.get(seg.id) ?? null,
    duration_seconds: seg.duration_seconds ?? null,
    context: seg.context ?? (sceneContext ? toDirectorContext(sceneContext) : null),
    director_adaptation: seg.director_adaptation
      ? { target_register: seg.director_adaptation.target_register ?? null }
      : null,
  };
}
