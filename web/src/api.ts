import { normalizeWavForTranscription } from "./transcriptionAudio";

export type SegmentInput = {
  id: string;
  text: string;
  duration_seconds?: number | null;
  start_seconds?: number | null;
  frame_image_data_url?: string | null;
  concepts?: string[] | null;
  speaker_id?: string | null;
  speaker_label?: string | null;
  frame_id?: string | null;
  timecode_seconds?: number | null;
  frame_sha256?: string | null;
  vision_enrichment?: boolean | null;
  translation?: string | null;
  context?: DirectorContextInput | null;
  director_adaptation?: { target_register?: string | null } | null;
  box?: LocalisationBox | null;
  source_text?: string | null;
  target_text?: string | null;
};

export type SpeakerMapping = Record<string, "anna" | "ashley" | "">;

export type LocalisationBox = { x1: number; y1: number; x2: number; y2: number };

// Visual localisation: crop-edit-composite of one sign box per frame. The
// viewer is read-only; outside_box_diff is machine-checked server-side.
export type VisualLocalisationSegmentResult = {
  id: string;
  status: string; // Localised | Failed
  localised_image_data_url?: string | null;
  box?: LocalisationBox | null;
  source_text?: string | null;
  target_text?: string | null;
  outside_box_diff?: number | null;
  error?: string | null;
  provenance: TranslationProvenance;
  usage: TranslationUsage;
  image_calls: number;
};

export type VisualLocalisationBatch = {
  segments: VisualLocalisationSegmentResult[];
  usage_total: TranslationUsage;
  image_calls_total: number;
};

export async function postVisualLocalisation(segments: SegmentInput[]): Promise<VisualLocalisationBatch> {
  const visualSegments = segments.map((s) => ({
    id: s.id,
    frame_image_data_url: s.frame_image_data_url ?? null,
    box: s.box ?? null,
    source_text: s.source_text ?? null,
    target_text: s.target_text ?? null,
  }));
  return postJson<VisualLocalisationBatch>("/api/visual-localisation", { segments: visualSegments });
}

// Caller-supplied DirectorContext on an input segment. Null fields are omitted
// by the server, so every field is optional here too.
export type DirectorContextInput = {
  speaker_id?: string | null;
  speaker_label?: string | null;
  emotion_tone_label?: string | null;
  pacing_label?: string | null;
  on_screen_text?: string[] | null;
  scene_summary?: string | null;
  speaker_description?: string | null;
  burned_in_text?: string[] | null;
};

export type StatusResponse = {
  hasKey: boolean;
  model: string;
  baseUrl: string;
  reasoningEffort: string;
};

export type TranslationProvenance = {
  model_id: string;
  endpoint_host: string;
  timestamp_utc: string;
  prompt_template_version: string;
};

export type TranslationUsage = {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
};

export type TranslatedSegment = {
  id: string;
  text: string;
  translation: string;
  provenance: TranslationProvenance;
  usage: TranslationUsage;
};

export type TranslateBatch = {
  source_language: string;
  target_language: string;
  segments: TranslatedSegment[];
  usage_total: TranslationUsage;
};

export type QaAttempt = {
  attempt_number: number;
  kind: string; // Initial | Revision
  translation: string;
  estimated_seconds: number;
  window_seconds: number;
  max_chars: number;
  verdict: string; // Pass | Fail (combined)
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
  model_id: string;
  timing_verdict: string; // Pass | Fail
  meaning_verdict: string; // Pass | Fail | Error
  meaning_issues: string[] | null;
  meaning_explanation: string | null;
  meaning_model_id: string | null;
  meaning_prompt_template_version: string | null;
  meaning_timestamp_utc: string | null;
  meaning_prompt_tokens: number;
  meaning_completion_tokens: number;
  meaning_total_tokens: number;
  meaning_endpoint_host: string | null;
};

export type QaSegmentReport = {
  id: string;
  text: string;
  window_seconds: number;
  first_attempt: QaAttempt;
  revisions: QaAttempt[];
  final_verdict: string;
  final_translation: string;
  token_usage: TranslationUsage;
};

export type QaTotals = {
  total_segments: number;
  first_pass_count: number;
  final_pass_count: number;
  total_revisions: number;
  first_pass_rate: number;
  final_pass_rate: number;
  total_tokens: number;
  prompt_tokens: number;
  completion_tokens: number;
};

export type QaReport = {
  source_language: string;
  target_language: string;
  qa_options: {
    max_attempts: number;
    tolerance_fraction: number;
    target_cps: number;
    source_cps: number;
    reasoning_effort: string | null;
  };
  segments: QaSegmentReport[];
  totals: QaTotals;
};

// Null context fields are omitted by the server (WhenWritingNull), so every
// field below is optional: absence means unknown, never a default.
export type SceneRegion = {
  concept: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  frame_width: number;
  frame_height: number;
  mask_x?: number;
  mask_y?: number;
  mask_data?: string;
};

export type DirectorContext = {
  speaker_id?: string;
  speaker_label?: string;
  emotion_tone_label?: string;
  pacing_label?: string;
  on_screen_text?: string[];
  scene_summary?: string;
  regions?: SceneRegion[];
  speaker_description?: string;
  burned_in_text?: string[];
};

export type VisionProvenance = {
  requested_model_id: string;
  model_id: string;
  endpoint_host: string;
  timestamp_utc: string;
  prompt_template_version: string;
};

export type VisionUsage = {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
  reasoning_tokens?: number;
  cached_tokens?: number;
};

export type SceneSegmentResult = {
  id: string;
  status: string; // Parsed | Empty | Failed
  context?: DirectorContext;
  error?: string;
  provenance: TranslationProvenance;
  usage: TranslationUsage;
  images_processed: number;
  raw_content?: string;
  frame_id?: string;
  timecode_seconds?: number;
  frame_sha256?: string;
  vision_provenance?: VisionProvenance;
  vision_usage?: VisionUsage;
  vision_raw_content?: string;
};

export type SceneContextBatch = {
  segments: SceneSegmentResult[];
  usage_total: TranslationUsage;
  images_processed_total: number;
  vision_usage_total?: VisionUsage;
};

async function postJson<T>(path: string, body: unknown, headers?: Record<string, string>): Promise<T> {
  const r = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
  if (!r.ok) {
    const bodyText = await r.text().catch(() => "");
    throw new Error(`POST ${path} ${r.status}: ${bodyText.slice(0, 600)}`);
  }
  return (await r.json()) as T;
}

export async function postSceneContext(segments: SegmentInput[]): Promise<SceneContextBatch> {
  const sceneSegments = segments.map((s) => ({
    id: s.id,
    text: s.text,
    frame_image_data_url: s.frame_image_data_url ?? null,
    concepts: s.concepts ?? null,
    speaker_id: s.speaker_id ?? null,
    speaker_label: s.speaker_label ?? null,
    frame_id: s.frame_id ?? null,
    timecode_seconds: s.timecode_seconds ?? null,
    frame_sha256: s.frame_sha256 ?? null,
    vision_enrichment: s.vision_enrichment ?? null,
  }));
  return postJson<SceneContextBatch>("/api/scene-context", { segments: sceneSegments });
}

// Director Mode Stage B2: register adaptation. Fields the server omits when
// null (WhenWritingNull) are optional: absence means not attempted / unknown.
export type DirectorAdaptationUsage = {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
  reasoning_tokens?: number;
  cached_tokens?: number;
};

export type DirectorMeaningResult = {
  verdict: string; // Pass | Fail | Error | NotEvaluated
  issues: string[];
  explanation: string;
  model_id?: string;
  prompt_template_version?: string;
  timestamp_utc?: string;
  usage: TranslationUsage;
};

export type DirectorAdaptationSegmentResult = {
  id: string;
  status: string; // Passed | Failed | NotRequested
  text?: string;
  original_translation?: string;
  adapted_translation?: string;
  changed?: boolean;
  target_register?: string;
  context_fields_used: string[];
  error?: string;
  provenance?: TranslationProvenance;
  usage?: DirectorAdaptationUsage;
  timing_verdict?: string; // Pass | Fail
  estimated_seconds?: number;
  window_seconds?: number;
  meaning?: DirectorMeaningResult;
  raw_content?: string;
};

export type DirectorAdaptationBatch = {
  source_language: string;
  target_language: string;
  segments: DirectorAdaptationSegmentResult[];
  usage_total: DirectorAdaptationUsage;
  meaning_usage_total: TranslationUsage;
  qa_options?: QaReport["qa_options"];
};

export type DirectorAdaptationInput = {
  id: string;
  text: string | null;
  translation: string | null;
  duration_seconds: number | null;
  context: DirectorContextInput | null;
  director_adaptation: { target_register: string | null } | null;
};

export interface QaRequestOptions {
  maxAttempts?: number;
  toleranceFraction?: number;
  targetCharactersPerSecond?: number;
  reasoningEffort?: string;
}

export interface AuditionRequestOptions {
  speedRetake?: { enabled: boolean; cap: number; max_retakes_per_segment: number };
  emotionTags?: { enabled: boolean };
  qaOptions?: QaRequestOptions;
  labelsById?: Map<string, { emotion_tone_label?: string | null; pacing_label?: string | null }>;
}

export async function postDirectorAdaptation(
  segments: DirectorAdaptationInput[],
  from = "en",
  to = "es",
  options?: QaRequestOptions,
): Promise<DirectorAdaptationBatch> {
  return postJson<DirectorAdaptationBatch>("/api/director-adaptation", {
    segments,
    sourceLanguage: from,
    targetLanguage: to,
    ...(options ? { options } : {}),
  });
}

// Audition casting: takes whose meaning gate ran before synthesis; timing is
// measured audio duration vs the stamped window. Failed takes stay listed.
export type AuditionCastVoice = {
  speaker_id: string;
  voice_id: string;
  voice_name: string;
};

export type AuditionTake = {
  take_number: number;
  kind: string; // Initial | Revision | Alternate | SpeedVariant
  text: string;
  meaning_verdict: string; // Pass | Fail | Error | NotEvaluated
  meaning_issues: string[] | null;
  meaning_explanation: string | null;
  synthesized: boolean;
  measured_seconds?: number;
  window_seconds: number;
  timing_verdict: string; // Pass | Fail | NotSynthesized
  is_winner: boolean;
  audio_file_name?: string;
  synthesis_note?: string;
  translation_tokens: number;
  meaning_tokens: number;
  elevenlabs_characters: number;
  speed_factor?: number | null; // set only on the speed-variant take
  speed_requested?: number;
  computed_factor?: number | null;
  calibration_corrected?: boolean;
  emotion_tag?: string | null;
  audio_sha256?: string;
  audio_data_url?: string;
  audio_url?: string;
  audio_transport_omitted?: string;
};

export type AuditionSegmentReport = {
  id: string;
  text: string;
  speaker_id?: string;
  cast_voice?: AuditionCastVoice;
  window_seconds: number;
  takes: AuditionTake[];
  cast: boolean;
  winner_take_number?: number;
  winner_file_name?: string;
  note?: string;
};

export type AuditionReport = {
  source_language: string;
  target_language: string;
  cast: AuditionCastVoice[];
  segments: AuditionSegmentReport[];
  totals: {
    total_segments: number;
    lines_cast: number;
    takes_synthesized: number;
    meta_calls: number;
    translation_tokens: number;
    meaning_tokens: number;
    elevenlabs_characters: number;
    elevenlabs_cap_hit: boolean;
    meta_cap_hit: boolean;
  };
  qa_options?: QaReport["qa_options"];
  speed_retake?: { enabled: boolean; cap: number; max_retakes_per_segment: number } | null;
};

type StageRequestOptions = AuditionRequestOptions & { speakerMapping?: SpeakerMapping; idempotencyKey?: string };

function mappedSpeaker(segment: SegmentInput, options?: StageRequestOptions): string | null {
  return options?.speakerMapping?.[segment.speaker_id ?? ""] || segment.speaker_id || null;
}

function emotionLabel(segment: SegmentInput, options?: StageRequestOptions): string | null {
  return options?.labelsById?.get(segment.id)?.emotion_tone_label ?? segment.context?.emotion_tone_label ?? null;
}

function pacingLabel(segment: SegmentInput, options?: StageRequestOptions): string | null {
  return options?.labelsById?.get(segment.id)?.pacing_label ?? segment.context?.pacing_label ?? null;
}

function auditionSegmentBody(segment: SegmentInput, options?: StageRequestOptions) {
  return {
    id: segment.id,
    text: segment.text,
    duration_seconds: segment.duration_seconds ?? null,
    speaker_id: mappedSpeaker(segment, options),
    emotion_tone_label: emotionLabel(segment, options),
    pacing_label: pacingLabel(segment, options),
  };
}

function addOption(body: Record<string, unknown>, key: string, value: unknown): void {
  if (value !== undefined) body[key] = value;
}

function stageOptionsBody(options?: StageRequestOptions): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  addOption(body, "options", options?.qaOptions);
  addOption(body, "speedRetake", options?.speedRetake);
  addOption(body, "emotionTags", options?.emotionTags);
  addOption(body, "speaker_mapping", options?.speakerMapping);
  return body;
}

export async function postAudition(
  segments: SegmentInput[],
  from = "en",
  to = "es",
  options?: StageRequestOptions,
): Promise<AuditionReport> {
  return postJson<AuditionReport>("/api/audition", {
    segments: segments.map((segment) => auditionSegmentBody(segment, options)),
    sourceLanguage: from,
    targetLanguage: to,
    ...stageOptionsBody(options),
  }, options?.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined);
}

// Transcribe stage: WAV in, segments in the app input shape out. The ASR
// endpoint reports no token usage; the server omits usage (null) and marks
// usage_reported false — never zero.
export type TranscribeSegment = {
  id: string;
  text: string;
  duration_seconds: number;
  speaker?: string | null;
  start_seconds?: number | null;
};

export type TranscribeResult = {
  model: string;
  endpoint_host: string;
  timestamp_utc: string;
  session_id?: string | null;
  transcript: string;
  audio_duration_ms: number;
  segments: TranscribeSegment[];
  usage_reported: boolean;
};

export interface DubExportPlacement {
  id: string;
  start_seconds: number;
  audio: Blob;
  file_name: string;
}

export async function postDubExport(
  video: File,
  segmentIds: string[],
  placements: DubExportPlacement[],
): Promise<Blob> {
  const form = new FormData();
  form.append("video", video, video.name || "original.mp4");
  form.append("segment_ids", JSON.stringify(segmentIds));
  form.append("placements", JSON.stringify(placements.map((placement, index) => ({
    id: placement.id,
    start_seconds: placement.start_seconds,
    audio_field: `audio_${index}`,
  }))));
  placements.forEach((placement, index) => {
    form.append(`audio_${index}`, placement.audio, placement.file_name);
  });
  const r = await fetch("/api/export-dub", { method: "POST", body: form });
  if (!r.ok) {
    const bodyText = await r.text().catch(() => "");
    throw new Error(`POST /api/export-dub ${r.status}: ${bodyText.slice(0, 600)}`);
  }
  const blob = await r.blob();
  if (blob.size === 0) throw new Error("POST /api/export-dub returned an empty MP4.");
  return blob;
}

export async function postTranscribe(audio: File): Promise<TranscribeResult> {
  const supportedAudio = await normalizeWavForTranscription(audio);
  const form = new FormData();
  form.append("audio", supportedAudio, supportedAudio.name || "audio.wav");
  const r = await fetch("/api/transcribe", { method: "POST", body: form });
  if (!r.ok) {
    const bodyText = await r.text().catch(() => "");
    throw new Error(`POST /api/transcribe ${r.status}: ${bodyText.slice(0, 600)}`);
  }
  return (await r.json()) as TranscribeResult;
}

// Pipeline: one orchestrated run over the existing lanes. Each stage is
// Ran | SkippedByFlag | Failed | NotRun; a flag-skipped stage is never a pass.
export type PipelineStage = {
  stage: string; // transcribe | qa | scene | audition
  status: string; // Ran | SkippedByFlag | Failed | NotRun
  note?: string | null;
  error?: string | null;
};

export type PipelineReport = {
  source_language: string;
  target_language: string;
  stages: PipelineStage[];
  segments: TranscribeSegment[];
  transcribe?: TranscribeResult | null;
  qa?: QaReport | null;
  scene?: SceneContextBatch | null;
  audition?: AuditionReport | null;
};

function pipelineSegmentBody(segment: SegmentInput) {
  return {
    id: segment.id,
    text: segment.text,
    duration_seconds: segment.duration_seconds ?? null,
    speaker: segment.speaker_id ?? null,
    start_seconds: segment.start_seconds ?? null,
  };
}

function pipelineSceneSegmentBody(segment: SegmentInput) {
  return {
    id: segment.id,
    text: segment.text,
    frame_image_data_url: segment.frame_image_data_url ?? null,
    concepts: segment.concepts ?? null,
    speaker_id: segment.speaker_id ?? null,
    speaker_label: segment.speaker_label ?? null,
    frame_id: segment.frame_id ?? null,
    timecode_seconds: segment.timecode_seconds ?? null,
    frame_sha256: segment.frame_sha256 ?? null,
    vision_enrichment: segment.vision_enrichment ?? null,
  };
}

function pipelineBaseBody(
  flags: { scene: boolean; audition: boolean },
  from: string,
  to: string,
  options?: StageRequestOptions,
): Record<string, unknown> {
  return {
    scene: flags.scene,
    audition: flags.audition,
    sourceLanguage: from,
    targetLanguage: to,
    ...stageOptionsBody(options),
  };
}

async function addPipelineAudio(body: Record<string, unknown>, audio: File): Promise<void> {
  const supportedAudio = await normalizeWavForTranscription(audio);
  body.audio_base64 = await fileToBase64(supportedAudio);
  body.audio_file_name = supportedAudio.name || "audio.wav";
}

export async function postPipeline(
  audio: File | null,
  segments: SegmentInput[],
  sceneSegments: SegmentInput[],
  flags: { scene: boolean; audition: boolean },
  from = "en",
  to = "es",
  options?: StageRequestOptions,
  idempotencyKey?: string,
): Promise<PipelineReport> {
  const body = pipelineBaseBody(flags, from, to, options);
  if (audio) await addPipelineAudio(body, audio);
  else body.segments = segments.map(pipelineSegmentBody);
  if (sceneSegments.length > 0) body.scene_segments = sceneSegments.map(pipelineSceneSegmentBody);
  return postJson<PipelineReport>("/api/pipeline", body, idempotencyKey ? { "Idempotency-Key": idempotencyKey } : undefined);
}

function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("Failed to read audio file"));
    reader.onload = () => {
      const result = reader.result as string;
      const comma = result.indexOf(",");
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.readAsDataURL(file);
  });
}

export interface CopilotSettings {
  max_revisions?: number | null;
  tolerance_percent?: number | null;
  target_cps?: number | null;
  speed_retake_enabled?: boolean | null;
  speed_cap?: number | null;
  emotion_tags_enabled?: boolean | null;
}

export interface CopilotSegmentUpdate {
  id: string;
  concepts?: string[] | null;
  vision_enrichment?: boolean | null;
  director_register?: string | null;
  sign_source_text?: string | null;
  sign_target_text?: string | null;
}

export interface CopilotProposal {
  template: string;
  explanation: string;
  settings: CopilotSettings;
  segment_updates: CopilotSegmentUpdate[];
  missing_inputs: string[];
  warnings: string[];
  model: string;
  usage?: { PromptTokens?: number; CompletionTokens?: number; TotalTokens?: number; prompt_tokens?: number; completion_tokens?: number; total_tokens?: number } | null;
  prompt_template_version: string;
}

export interface CopilotRequest {
  goal: string;
  source_language: string;
  target_language: string;
  settings: CopilotSettings;
  segments: Array<{
    id: string;
    text: string;
    duration_seconds?: number | null;
    speaker_id?: string | null;
    has_frame: boolean;
    concepts?: string[] | null;
    vision_enrichment: boolean;
    director_register?: string | null;
    has_sign_box: boolean;
    sign_source_text?: string | null;
    sign_target_text?: string | null;
  }>;
  report_summary?: string | null;
}

export async function postCopilotProposal(request: CopilotRequest): Promise<CopilotProposal> {
  return postJson<CopilotProposal>("/api/copilot/propose", request);
}

export async function fetchStatus(): Promise<StatusResponse> {
  const r = await fetch("/api/status");
  if (!r.ok) throw new Error(`GET /api/status ${r.status}`);
  return (await r.json()) as StatusResponse;
}

export async function fetchModels(): Promise<{ id: string }[]> {
  const r = await fetch("/api/models");
  if (!r.ok) {
    const body = await r.text().catch(() => "");
    throw new Error(`GET /api/models ${r.status}: ${body.slice(0, 300)}`);
  }
  return (await r.json()) as { id: string }[];
}

export async function postTranslate(
  segments: SegmentInput[],
  from = "en",
  to = "es",
): Promise<TranslateBatch> {
  return postJson<TranslateBatch>("/api/translate", {
    segments,
    sourceLanguage: from,
    targetLanguage: to,
  });
}

function buildQaOptions(
  options?: QaRequestOptions & { targetCps?: number },
): Record<string, unknown> | undefined {
  if (!options) return undefined;
  const opts: Record<string, unknown> = {};
  if (options.maxAttempts != null) opts.maxAttempts = options.maxAttempts;
  if (options.toleranceFraction != null) opts.toleranceFraction = options.toleranceFraction;
  const cps = options.targetCharactersPerSecond ?? options.targetCps;
  if (cps != null) opts.targetCharactersPerSecond = cps;
  if (options.reasoningEffort != null) opts.reasoningEffort = options.reasoningEffort;
  return Object.keys(opts).length > 0 ? opts : undefined;
}

export async function postQa(
  segments: SegmentInput[],
  from = "en",
  to = "es",
  options?: QaRequestOptions & { targetCps?: number },
): Promise<QaReport> {
  const body: Record<string, unknown> = { segments, sourceLanguage: from, targetLanguage: to };
  const opts = buildQaOptions(options);
  if (opts) body.options = opts;
  return postJson<QaReport>("/api/qa", body);
}
