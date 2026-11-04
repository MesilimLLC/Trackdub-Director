import type {
  AuditionReport,
  CopilotProposal,
  CopilotRequest,
  CopilotSegmentUpdate,
  CopilotSettings,
  DirectorAdaptationBatch,
  QaReport,
  SceneContextBatch,
  SegmentInput,
  VisualLocalisationBatch,
} from "./api";
import type { ChangeKind, RunSettings } from "./workbench";

export interface CopilotReportState {
  qa: QaReport | null;
  scene: SceneContextBatch | null;
  director: DirectorAdaptationBatch | null;
  audition: AuditionReport | null;
  visual: VisualLocalisationBatch | null;
}

export function buildCopilotRequest(input: {
  goal: string;
  from: string;
  to: string;
  settings: RunSettings;
  segments: SegmentInput[];
  reports: CopilotReportState;
}): CopilotRequest {
  return {
    goal: input.goal.trim(),
    source_language: input.from,
    target_language: input.to,
    settings: copilotSettings(input.settings),
    segments: input.segments.map(copilotSegmentSummary),
    report_summary: reportSummary(input.reports),
  };
}

function copilotSettings(settings: RunSettings): CopilotSettings {
  return {
    max_revisions: settings.maxRevisions,
    tolerance_percent: settings.tolerancePercent,
    target_cps: settings.targetCps === "" ? null : Number(settings.targetCps),
    speed_retake_enabled: settings.speedRetakeEnabled,
    speed_cap: settings.speedCap,
    emotion_tags_enabled: settings.emotionTagsEnabled,
  };
}

function copilotSegmentSummary(segment: SegmentInput) {
  return {
    id: segment.id,
    text: segment.text,
    duration_seconds: segment.duration_seconds,
    speaker_id: segment.speaker_id,
    has_frame: Boolean(segment.frame_image_data_url),
    concepts: segment.concepts,
    vision_enrichment: segment.vision_enrichment ?? false,
    director_register: segment.director_adaptation?.target_register ?? null,
    has_sign_box: Boolean(segment.box),
    sign_source_text: segment.source_text,
    sign_target_text: segment.target_text,
  };
}

function reportSummary(reports: CopilotReportState): string {
  return [
    qaSummary(reports.qa),
    statuses("Scene", reports.scene?.segments.map((s) => s.status)),
    statuses("Director", reports.director?.segments.map((s) => s.status)),
    auditionSummary(reports.audition),
    statuses("Visual", reports.visual?.segments.map((s) => s.status)),
  ].join(" | ").slice(0, 4000);
}

function qaSummary(qa: QaReport | null): string {
  return qa ? `QA final ${qa.totals.final_pass_count}/${qa.totals.total_segments}, revisions ${qa.totals.total_revisions}` : "QA not run";
}

function auditionSummary(audition: AuditionReport | null): string {
  return audition ? `Audition cast ${audition.totals.lines_cast}/${audition.totals.total_segments}` : "Audition not run";
}

function statuses(label: string, values: string[] | undefined): string {
  return values ? `${label} statuses ${values.join(",")}` : `${label} not run`;
}

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

export type CopilotApplyPlan =
  | { error: string; clearProposal: boolean }
  | { merged: { settings: RunSettings; changes: ChangeKind[] }; segments: SegmentInput[] | null };

export function planCopilotApply(input: {
  proposalState: { proposal: CopilotProposal; fingerprint: string } | null;
  currentFingerprint: string;
  segments: SegmentInput[];
  settings: RunSettings;
}): CopilotApplyPlan {
  if (!input.proposalState || input.proposalState.fingerprint !== input.currentFingerprint) {
    return { error: "Copilot proposal is stale because the workbench changed. Prepare a new proposal.", clearProposal: true };
  }
  const { proposal } = input.proposalState;
  if (proposal.segment_updates.length > 0 && hasDuplicateSegmentIds(input.segments)) {
    return { error: "Copilot segment updates cannot be applied while segment IDs are duplicated. Make the segment IDs unique first.", clearProposal: false };
  }
  return {
    merged: mergeCopilotSettings(input.settings, proposal.settings),
    segments: proposal.segment_updates.length > 0 ? applyCopilotUpdates(input.segments, proposal.segment_updates) : null,
  };
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
