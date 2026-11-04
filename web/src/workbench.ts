import type { AuditionReport, LocalisationBox, SegmentInput } from "./api";

export type ReportKind = "batch" | "qa" | "scene" | "director" | "audition" | "visual" | "pipeline";
export type ChangeKind =
  | "structure" | "text" | "duration" | "speaker" | "language" | "qaOptions"
  | "frame" | "sceneConfig" | "director" | "auditionOptions" | "visual" | "audio" | "start";

export interface RunSettings {
  maxRevisions: number;
  tolerancePercent: number;
  targetCps: string;
  speedRetakeEnabled: boolean;
  speedCap: number;
  emotionTagsEnabled: boolean;
}

export const DEFAULT_RUN_SETTINGS: RunSettings = {
  maxRevisions: 3,
  tolerancePercent: 10,
  targetCps: "",
  speedRetakeEnabled: false,
  speedCap: 1.1,
  emotionTagsEnabled: true,
};

const ALL_REPORTS: ReportKind[] = ["batch", "qa", "scene", "director", "audition", "visual", "pipeline"];

const INVALIDATION: Record<ChangeKind, ReportKind[]> = {
  structure: ALL_REPORTS,
  text: ["batch", "qa", "director", "audition", "pipeline"],
  duration: ["qa", "director", "audition", "pipeline"],
  speaker: ["scene", "director", "audition", "pipeline"],
  language: ["batch", "qa", "director", "audition", "pipeline"],
  qaOptions: ["qa", "director", "audition", "pipeline"],
  frame: ["scene", "director", "audition", "pipeline", "visual"],
  sceneConfig: ["scene", "director", "audition", "pipeline"],
  director: ["director"],
  auditionOptions: ["audition", "pipeline"],
  visual: ["visual"],
  audio: ["pipeline"],
  start: [],
};

export function reportsForChanges(changes: Iterable<ChangeKind>): ReportKind[] {
  const kinds = new Set<ReportKind>();
  for (const change of changes) {
    for (const kind of INVALIDATION[change]) kinds.add(kind);
  }
  return [...kinds];
}

function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

function changed(a: unknown, b: unknown): boolean {
  return !sameValue(a, b);
}

function sceneConfigChanged(a: SegmentInput, b: SegmentInput): boolean {
  return changed(a.concepts, b.concepts)
    || changed(a.vision_enrichment, b.vision_enrichment)
    || changed(a.frame_id, b.frame_id)
    || changed(a.timecode_seconds, b.timecode_seconds)
    || changed(a.frame_sha256, b.frame_sha256);
}

function directorChanged(a: SegmentInput, b: SegmentInput): boolean {
  return changed(a.director_adaptation, b.director_adaptation)
    || changed(a.context, b.context)
    || changed(a.translation, b.translation);
}

function visualChanged(a: SegmentInput, b: SegmentInput): boolean {
  return changed(a.box, b.box) || changed(a.source_text, b.source_text) || changed(a.target_text, b.target_text);
}

function speakerChanged(a: SegmentInput, b: SegmentInput): boolean {
  return changed(a.speaker_id, b.speaker_id) || changed(a.speaker_label, b.speaker_label);
}

function classifyPair(a: SegmentInput, b: SegmentInput, changes: Set<ChangeKind>): void {
  if (a.text !== b.text) changes.add("text");
  if (changed(a.duration_seconds, b.duration_seconds)) changes.add("duration");
  if (changed(a.start_seconds, b.start_seconds)) changes.add("start");
  if (speakerChanged(a, b)) changes.add("speaker");
  if (changed(a.frame_image_data_url, b.frame_image_data_url)) changes.add("frame");
  if (sceneConfigChanged(a, b)) changes.add("sceneConfig");
  if (directorChanged(a, b)) changes.add("director");
  if (visualChanged(a, b)) changes.add("visual");
}

function hasStructureChange(before: SegmentInput[], after: SegmentInput[]): boolean {
  return before.length !== after.length || before.some((segment, i) => segment.id !== after[i]?.id);
}

export function classifySegmentChanges(before: SegmentInput[], after: SegmentInput[]): ChangeKind[] {
  if (hasStructureChange(before, after)) return ["structure"];
  const changes = new Set<ChangeKind>();
  before.forEach((segment, i) => classifyPair(segment, after[i], changes));
  return [...changes];
}

export function qaOptionsFromSettings(settings: RunSettings): {
  maxAttempts: number;
  toleranceFraction: number;
  targetCharactersPerSecond?: number;
} {
  const parsedCps = Number.parseFloat(settings.targetCps);
  return {
    maxAttempts: Math.max(0, Math.trunc(settings.maxRevisions) || 0),
    toleranceFraction: Math.max(0, settings.tolerancePercent || 0) / 100,
    ...(Number.isFinite(parsedCps) && parsedCps > 0 ? { targetCharactersPerSecond: parsedCps } : {}),
  };
}

export function speedRetakeFromSettings(settings: RunSettings): {
  enabled: boolean;
  cap: number;
  max_retakes_per_segment: number;
} {
  return {
    enabled: settings.speedRetakeEnabled,
    cap: Math.min(1.2, Math.max(1, settings.speedCap || 1.1)),
    max_retakes_per_segment: 1,
  };
}

export function emotionTagsFromSettings(settings: RunSettings): { enabled: boolean } {
  return { enabled: settings.emotionTagsEnabled };
}

export function callEstimates(segments: SegmentInput[], settings: RunSettings): Record<string, string> {
  const count = segments.length;
  const revisions = Math.max(0, Math.trunc(settings.maxRevisions) || 0);
  const framed = segments.filter((s) => Boolean(s.frame_image_data_url));
  const concepts = framed.reduce((sum, s) => sum + (s.concepts?.filter(Boolean).length ?? 0), 0);
  const vision = framed.filter((s) => s.vision_enrichment).length;
  const directed = segments.filter((s) => Boolean(s.director_adaptation?.target_register)).length;
  const visual = segments.filter((s) => Boolean(s.frame_image_data_url && s.box && s.source_text && s.target_text)).length;
  return {
    translate: `${count} Meta call${count === 1 ? "" : "s"}`,
    qa: `≤ ${2 * (revisions + 1) * count} Meta calls`,
    scene: `${concepts} segmentation + ${vision} vision call${vision === 1 ? "" : "s"}`,
    director: `≤ ${2 * directed} Meta calls`,
    audition: "≤ 40 Meta calls · ≤ 3,000 ElevenLabs chars",
    visual: `${visual} image call${visual === 1 ? "" : "s"}`,
    pipeline: "Transcribe + QA, plus selected Scene/Audition bounds",
  };
}

export function nextSegmentId(segments: SegmentInput[]): string {
  const used = new Set(segments.map((segment) => segment.id));
  let number = segments.length + 1;
  while (used.has(`seg-${String(number).padStart(3, "0")}`)) number++;
  return `seg-${String(number).padStart(3, "0")}`;
}

export interface DubExportPlacementDraft {
  id: string;
  start_seconds: number;
  audio_data_url?: string;
  audio_url?: string;
  file_name: string;
}

type AuditionSegment = AuditionReport["segments"][number];
type AuditionTake = AuditionSegment["takes"][number];

function duplicateProblems(segments: SegmentInput[]): string[] {
  const seen = new Set<string>();
  const problems: string[] = [];
  if (segments.length === 0) problems.push("Add at least one segment.");
  for (const segment of segments) {
    if (seen.has(segment.id)) problems.push(`Duplicate segment id '${segment.id}'.`);
    seen.add(segment.id);
  }
  return problems;
}

function auditionReportsById(audition: AuditionReport | null): Map<string, AuditionSegment> {
  const reports = new Map<string, AuditionSegment>();
  for (const report of audition?.segments ?? []) {
    if (!reports.has(report.id)) reports.set(report.id, report);
  }
  return reports;
}

function validStart(segment: SegmentInput): number | null {
  const start = segment.start_seconds;
  return start != null && Number.isFinite(start) && start >= 0 ? start : null;
}

function winningTake(report: AuditionSegment | undefined): AuditionTake | null {
  if (!report?.cast) return null;
  const winner = report.takes.find((take) => take.is_winner);
  return winner && report.winner_take_number === winner.take_number ? winner : null;
}

function placementFor(
  segment: SegmentInput,
  reports: Map<string, AuditionSegment>,
): { placement: DubExportPlacementDraft } | { problem: string } {
  const start = validStart(segment);
  if (start === null) return { problem: `${segment.id} needs a start time.` };

  const report = reports.get(segment.id);
  const winner = winningTake(report);
  if (!winner) return { problem: `${segment.id} has no winning audition take.` };
  if (!winner.audio_data_url && !winner.audio_url) return { problem: `${segment.id} winner audio is unavailable from browser transport.` };

  return {
    placement: {
      id: segment.id,
      start_seconds: start,
      ...(winner.audio_data_url ? { audio_data_url: winner.audio_data_url } : { audio_url: winner.audio_url }),
      file_name: winner.audio_file_name ?? report!.winner_file_name ?? `${segment.id}-take${winner.take_number}.wav`,
    },
  };
}

export function buildDubExportPlacements(
  segments: SegmentInput[],
  audition: AuditionReport | null,
): { placements: DubExportPlacementDraft[]; problems: string[] } {
  const problems = duplicateProblems(segments);
  const reports = auditionReportsById(audition);
  const placements: DubExportPlacementDraft[] = [];
  for (const segment of segments) {
    const result = placementFor(segment, reports);
    if ("problem" in result) problems.push(result.problem);
    else placements.push(result.placement);
  }
  return { placements: problems.length === 0 ? placements : [], problems };
}

export function dubExportReadiness(
  video: File | null,
  built: ReturnType<typeof buildDubExportPlacements>,
  problems: string[],
): string {
  if (video && problems.length === 0) {
    return `${built.placements.length} winning take${built.placements.length === 1 ? "" : "s"} ready at explicit start times. The backend renders with ffmpeg and omits the original audio.`;
  }
  return problems.length > 0
    ? problems.join(" ")
    : "Choose the original video. Every segment needs a start time and a winning audition take.";
}

export function wavDataUrlToBlob(dataUrl: string): Blob {
  const match = /^data:audio\/wav;base64,([A-Za-z0-9+/=]+)$/.exec(dataUrl);
  if (!match) throw new Error("Winner audio is not a WAV data URL.");
  const binary = atob(match[1]);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new Blob([bytes], { type: "audio/wav" });
}

export function stripAudioTransport<T>(value: T): T {
  if (Array.isArray(value)) return value.map((item) => stripAudioTransport(item)) as T;
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if (key !== "audio_data_url" && key !== "audio_url") out[key] = stripAudioTransport(child);
    }
    return out as T;
  }
  return value;
}

export function downloadBlob(fileName: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

export function downloadText(fileName: string, text: string, type = "application/json"): void {
  downloadBlob(fileName, new Blob([text], { type }));
}

export function downloadJson(fileName: string, value: unknown): void {
  downloadText(fileName, JSON.stringify(value, null, 2));
}

export const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
export const MAX_IMAGE_DIMENSION = 4096;
export const MAX_IMAGE_PIXELS = 16_777_216;
export const MIN_BOX_PIXELS = 8;

export function validateImageMetadata(file: { size: number; type: string }, width: number, height: number): string | null {
  if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) return "Frame must be PNG, JPEG, or WebP.";
  if (file.size > MAX_IMAGE_BYTES) return "Frame exceeds the 10 MiB limit.";
  if (width > MAX_IMAGE_DIMENSION || height > MAX_IMAGE_DIMENSION || width * height > MAX_IMAGE_PIXELS) {
    return "Frame dimensions exceed 4096×4096 or 16 megapixels.";
  }
  return null;
}

export interface PointerPoint { x: number; y: number }

export function pointerToOriginal(
  clientX: number,
  clientY: number,
  rect: { left: number; top: number; width: number; height: number },
  natural: { width: number; height: number },
): PointerPoint {
  const x = ((clientX - rect.left) / Math.max(1, rect.width)) * natural.width;
  const y = ((clientY - rect.top) / Math.max(1, rect.height)) * natural.height;
  return {
    x: Math.min(natural.width, Math.max(0, Math.floor(x))),
    y: Math.min(natural.height, Math.max(0, Math.floor(y))),
  };
}

export function normalizeBox(a: PointerPoint, b: PointerPoint, natural: { width: number; height: number }): LocalisationBox | null {
  const box = {
    x1: Math.min(a.x, b.x),
    y1: Math.min(a.y, b.y),
    x2: Math.max(a.x, b.x),
    y2: Math.max(a.y, b.y),
  };
  box.x1 = Math.max(0, Math.min(natural.width, box.x1));
  box.y1 = Math.max(0, Math.min(natural.height, box.y1));
  box.x2 = Math.max(0, Math.min(natural.width, box.x2));
  box.y2 = Math.max(0, Math.min(natural.height, box.y2));
  return box.x2 - box.x1 >= MIN_BOX_PIXELS && box.y2 - box.y1 >= MIN_BOX_PIXELS ? box : null;
}
