import type { CopilotSegmentUpdate, CopilotSettings, SegmentInput } from "./api";
import type { ChangeKind, RunSettings } from "./workbench";

export function hasDuplicateSegmentIds(segments: SegmentInput[]): boolean {
  const seen = new Set<string>();
  for (const segment of segments) {
    if (seen.has(segment.id)) return true;
    seen.add(segment.id);
  }
  return false;
}

export function mergeCopilotSettings(
  current: RunSettings,
  proposed: CopilotSettings,
): { settings: RunSettings; changes: ChangeKind[] } {
  const settings: RunSettings = {
    ...current,
    maxRevisions: proposed.max_revisions ?? current.maxRevisions,
    tolerancePercent: proposed.tolerance_percent ?? current.tolerancePercent,
    targetCps: proposed.target_cps == null ? current.targetCps : String(proposed.target_cps),
    speedRetakeEnabled: proposed.speed_retake_enabled ?? current.speedRetakeEnabled,
    speedCap: proposed.speed_cap ?? current.speedCap,
    emotionTagsEnabled: proposed.emotion_tags_enabled ?? current.emotionTagsEnabled,
  };
  const changes: ChangeKind[] = [];
  if (qaSettingsChanged(current, settings)) changes.push("qaOptions");
  if (auditionSettingsChanged(current, settings)) changes.push("auditionOptions");
  return { settings, changes };
}

function qaSettingsChanged(a: RunSettings, b: RunSettings): boolean {
  return a.maxRevisions !== b.maxRevisions
    || a.tolerancePercent !== b.tolerancePercent
    || a.targetCps !== b.targetCps;
}

function auditionSettingsChanged(a: RunSettings, b: RunSettings): boolean {
  return a.speedRetakeEnabled !== b.speedRetakeEnabled
    || a.speedCap !== b.speedCap
    || a.emotionTagsEnabled !== b.emotionTagsEnabled;
}

export function applyCopilotUpdates(
  segments: SegmentInput[],
  updates: CopilotSegmentUpdate[],
): SegmentInput[] {
  const byId = new Map(updates.map((update) => [update.id, update] as const));
  return segments.map((segment) => {
    const update = byId.get(segment.id);
    return update ? segmentWithUpdate(segment, update) : segment;
  });
}

function segmentWithUpdate(segment: SegmentInput, update: CopilotSegmentUpdate): SegmentInput {
  return {
    ...segment,
    concepts: update.concepts ?? segment.concepts,
    vision_enrichment: update.vision_enrichment ?? segment.vision_enrichment,
    director_adaptation: directorAdaptation(segment, update),
    source_text: update.sign_source_text === undefined ? segment.source_text : update.sign_source_text,
    target_text: update.sign_target_text === undefined ? segment.target_text : update.sign_target_text,
  };
}

function directorAdaptation(segment: SegmentInput, update: CopilotSegmentUpdate) {
  if (update.director_register === undefined) return segment.director_adaptation;
  return update.director_register === null ? null : { target_register: update.director_register };
}
