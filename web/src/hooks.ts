import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  type AuditionReport,
  type DirectorAdaptationBatch,
  type CopilotProposal,
  type CopilotRequest,
  type PipelineReport,
  type QaReport,
  type QaSegmentReport,
  type SceneContextBatch,
  type SceneSegmentResult,
  type SegmentInput,
  type SpeakerMapping,
  type StatusResponse,
  type TranslateBatch,
  type VisualLocalisationBatch,
  fetchStatus,
  postAudition,
  postDirectorAdaptation,
  postCopilotProposal,
  postDubExport,
  postPipeline,
  postQa,
  postSceneContext,
  postTranscribe,
  postTranslate,
  postVisualLocalisation,
} from "./api";
import sampleSegments from "./sampleSegments.json";
import { prepareDirectorInput, resolveScene } from "./directorInput";
import { clearPendingIdempotencyKey, createIdempotencyKey, readPendingIdempotencyKey, savePendingIdempotencyKey } from "./idempotency";
import { errorMessage, parseSegments } from "./format";
import {
  type ReportKind,
  type RunSettings,
  buildDubExportPlacements,
  downloadBlob,
  emotionTagsFromSettings,
  qaOptionsFromSettings,
  speedRetakeFromSettings,
  wavDataUrlToBlob,
} from "./workbench";

export const DEFAULT_FROM = "en";
export const DEFAULT_TO = "es";

export type BusyState = "translate" | "qa" | "scene" | "director" | "audition" | "visual" | "pipeline" | "transcribe" | "copilot" | "export" | null;

export function useStatus() {
  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);

  const refreshStatus = useCallback(async () => {
    try {
      setStatus(await fetchStatus());
      setStatusError(null);
    } catch (e) {
      setStatusError(errorMessage(e));
    }
  }, []);

  useEffect(() => {
    let active = true;
    fetchStatus().then(
      (next) => {
        if (!active) return;
        setStatus(next);
        setStatusError(null);
      },
      (e: unknown) => {
        if (active) setStatusError(errorMessage(e));
      },
    );
    return () => {
      active = false;
    };
  }, []);

  return { status, statusError, refreshStatus };
}

function nextRequestId(ref: React.RefObject<number>): number {
  ref.current += 1;
  return ref.current;
}

function mapSegmentsById<T extends { id: string }, V>(segments: T[] | undefined, value: (segment: T) => V): Map<string, V> | null {
  return segments ? new Map(segments.map((segment) => [segment.id, value(segment)] as const)) : null;
}

function mapSegmentsByIndex<T, V>(segments: T[] | undefined, value: (segment: T) => V): Map<number, V> | null {
  return segments ? new Map(segments.map((segment, index) => [index, value(segment)] as const)) : null;
}

function reportSegments<T>(report: { segments: T[] } | null): T[] | undefined {
  return report?.segments;
}

export function useReports() {
  const [qa, setQa] = useState<QaReport | null>(null);
  const [batch, setBatch] = useState<TranslateBatch | null>(null);
  const [scene, setScene] = useState<SceneContextBatch | null>(null);
  const [director, setDirector] = useState<DirectorAdaptationBatch | null>(null);
  const [audition, setAudition] = useState<AuditionReport | null>(null);
  const [visual, setVisual] = useState<VisualLocalisationBatch | null>(null);
  const [pipeline, setPipeline] = useState<PipelineReport | null>(null);

  const clearKinds = useCallback((kinds: Iterable<ReportKind>) => {
    const setters: Record<ReportKind, () => void> = {
      qa: () => setQa(null),
      batch: () => setBatch(null),
      scene: () => setScene(null),
      director: () => setDirector(null),
      audition: () => setAudition(null),
      visual: () => setVisual(null),
      pipeline: () => setPipeline(null),
    };
    for (const kind of new Set(kinds)) setters[kind]();
  }, []);

  const reset = useCallback(() => {
    clearKinds(["batch", "qa", "scene", "director", "audition", "visual", "pipeline"]);
  }, [clearKinds]);

  const sceneById = useMemo(() => mapSegmentsByIndex(reportSegments(scene), (s) => s), [scene]);
  const sceneBySegId = useMemo(() => mapSegmentsById(reportSegments(scene), (s) => s), [scene]);
  const qaById = useMemo(() => mapSegmentsById(reportSegments(qa), (s) => s), [qa]);
  const batchById = useMemo(() => mapSegmentsById(reportSegments(batch), (s) => s.translation), [batch]);
  const directorById = useMemo(() => mapSegmentsById(reportSegments(director), (s) => s), [director]);
  const auditionById = useMemo(() => mapSegmentsById(reportSegments(audition), (s) => s), [audition]);
  const visualById = useMemo(() => mapSegmentsByIndex(reportSegments(visual), (s) => s), [visual]);

  return {
    qa, setQa, batch, setBatch, scene, setScene, director, setDirector,
    audition, setAudition, visual, setVisual, pipeline, setPipeline,
    reset, clearKinds, qaById, sceneById, sceneBySegId, batchById, directorById, auditionById, visualById,
  };
}

export function useSegments(onReset: () => void, setError: React.Dispatch<React.SetStateAction<string | null>>) {
  const [segments, setSegments] = useState<SegmentInput[]>(() => sampleSegments as SegmentInput[]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);
  const segmentsRequestRef = useRef(0);
  const segmentKeysRef = useRef(new WeakMap<SegmentInput, number>());
  const segmentKeySequenceRef = useRef(0);

  const keyForSegment = useCallback((segment: SegmentInput): number => {
    const keys = segmentKeysRef.current;
    let key = keys.get(segment);
    if (key === undefined) {
      key = nextRequestId(segmentKeySequenceRef);
      keys.set(segment, key);
    }
    return key;
  }, []);

  const inheritSegmentKeys = useCallback((previous: SegmentInput[], next: SegmentInput[]) => {
    for (let i = 0; i < Math.min(previous.length, next.length); i++) {
      if (next[i] !== previous[i]) segmentKeysRef.current.set(next[i], keyForSegment(previous[i]));
    }
    return next;
  }, [keyForSegment]);

  const loadSegments = useCallback((next: SegmentInput[]) => {
    nextRequestId(segmentsRequestRef);
    setSegments(next);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const replaceSegments = useCallback((next: SegmentInput[]) => {
    nextRequestId(segmentsRequestRef);
    setSegments((previous) => inheritSegmentKeys(previous, next));
    setError(null);
  }, [setError, inheritSegmentKeys]);

  const handleLoadSample = useCallback(() => {
    nextRequestId(segmentsRequestRef);
    setSegments(sampleSegments as SegmentInput[]);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const handleFile = useCallback(async (file: File) => {
    setError(null);
    const requestId = nextRequestId(segmentsRequestRef);
    try {
      const parsed = parseSegments(await file.text());
      if (requestId !== segmentsRequestRef.current) return;
      setSegments(parsed);
      onReset();
    } catch (e) {
      if (requestId !== segmentsRequestRef.current) return;
      setError(errorMessage(e));
    }
  }, [onReset, setError]);

  const onFileChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => handleSegmentFileSelection(e, fileInputRef, handleFile),
    [handleFile],
  );

  const toggle = useCallback((id: string) => {
    setExpanded((m) => ({ ...m, [id]: !m[id] }));
  }, []);

  return { segments, expanded, fileInputRef, handleLoadSample, onFileChange, toggle, loadSegments, replaceSegments, keyForSegment };
}

function handleSegmentFileSelection(
  event: React.ChangeEvent<HTMLInputElement>,
  inputRef: React.RefObject<HTMLInputElement | null>,
  handleFile: (file: File) => Promise<void>,
): void {
  const file = event.target.files?.[0];
  if (file) void handleFile(file);
  if (inputRef.current) inputRef.current.value = "";
}

export interface ActionsContext {
  segments: SegmentInput[];
  from: string;
  to: string;
  settings: RunSettings;
  refreshStatus: () => Promise<void>;
  setBatch: (batch: TranslateBatch) => void;
  setQa: (qa: QaReport) => void;
  setScene: (scene: SceneContextBatch) => void;
  setDirector: (director: DirectorAdaptationBatch) => void;
  setAudition: (audition: AuditionReport) => void;
  setVisual: (visual: VisualLocalisationBatch) => void;
  setPipeline: (report: PipelineReport) => void;
  loadSegments: (segments: SegmentInput[]) => void;
  audition: AuditionReport | null;
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  sceneBySegId: Map<string, SceneSegmentResult> | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  translateRequestRef: React.MutableRefObject<number>;
  qaRequestRef: React.MutableRefObject<number>;
  sceneRequestRef: React.MutableRefObject<number>;
  directorRequestRef: React.MutableRefObject<number>;
  auditionRequestRef: React.MutableRefObject<number>;
  visualRequestRef: React.MutableRefObject<number>;
  pipelineRequestRef: React.MutableRefObject<number>;
  transcribeRequestRef: React.MutableRefObject<number>;
  copilotRequestRef: React.MutableRefObject<number>;
}

export function useActions(context: ActionsContext) {
  const {
    segments, from, to, settings, refreshStatus, setBatch, setQa, setScene, setDirector, setAudition,
    setVisual, setPipeline, loadSegments, audition, qaById, batchById, sceneById, sceneBySegId, setError,
  } = context;
  const [busy, setBusy] = useState<BusyState>(null);
  const [auditionRetryPending, setAuditionRetryPending] = useState(() => Boolean(readPendingIdempotencyKey("audition")));
  const auditionKeyRef = useRef<string | null>(readPendingIdempotencyKey("audition"));
  const busyRef = useRef<BusyState>(null);

  const run = useCallback(async (kind: Exclude<BusyState, null>, action: () => Promise<void>) => {
    if (busyRef.current !== null) return;
    busyRef.current = kind;
    setBusy(kind);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      busyRef.current = null;
      setBusy(null);
      void refreshStatus();
    }
  }, [refreshStatus, setError]);

  const qaOptions = useMemo(() => qaOptionsFromSettings(settings), [settings]);
  const auditionOptions = useMemo(() => ({
    qaOptions,
    speedRetake: speedRetakeFromSettings(settings),
    emotionTags: emotionTagsFromSettings(settings),
  }), [qaOptions, settings]);
  const runTranslate = useCallback(async () => {
    const requestId = nextRequestId(context.translateRequestRef);
    await run("translate", async () => {
      const res = await postTranslate(segments, from, to);
      if (requestId === context.translateRequestRef.current) setBatch(res);
    });
  }, [context.translateRequestRef, run, segments, from, to, setBatch]);

  const runQa = useCallback(async () => {
    const requestId = nextRequestId(context.qaRequestRef);
    await run("qa", async () => {
      const res = await postQa(segments, from, to, qaOptions);
      if (requestId === context.qaRequestRef.current) setQa(res);
    });
  }, [context.qaRequestRef, run, segments, from, to, qaOptions, setQa]);

  const runScene = useCallback(async () => {
    const requestId = nextRequestId(context.sceneRequestRef);
    await run("scene", async () => {
      const res = await postSceneContext(segments);
      if (requestId === context.sceneRequestRef.current) setScene(res);
    });
  }, [context.sceneRequestRef, run, segments, setScene]);

  const runDirector = useCallback(async () => {
    const requestId = nextRequestId(context.directorRequestRef);
    await run("director", async () => {
      const lookups = { qaById, batchById, sceneById, sceneBySegId };
      const prepared = segments.map((seg, idx) => prepareDirectorInput(seg, idx, segments.length, lookups));
      const res = await postDirectorAdaptation(prepared, from, to, qaOptions);
      if (requestId === context.directorRequestRef.current) setDirector(res);
    });
  }, [context.directorRequestRef, run, segments, from, to, qaOptions, setDirector, qaById, batchById, sceneById, sceneBySegId]);

  const runAudition = useCallback(async (speakerMapping: SpeakerMapping = {}) => {
    const requestId = nextRequestId(context.auditionRequestRef);
    if (!auditionKeyRef.current) {
      const key = createIdempotencyKey();
      if (!savePendingIdempotencyKey("audition", key)) {
        setError("Recovery storage is unavailable, so the paid Audition request was not sent.");
        return;
      }
      auditionKeyRef.current = key;
    }
    const idempotencyKey = auditionKeyRef.current;
    setAuditionRetryPending(true);
    await run("audition", async () => {
      const labelsById = new Map<string, { emotion_tone_label?: string | null; pacing_label?: string | null }>();
      segments.forEach((seg, idx) => {
        const scene = resolveScene(seg, idx, { sceneById, sceneBySegId }, segments.length);
        labelsById.set(seg.id, {
          emotion_tone_label: scene?.context?.emotion_tone_label ?? seg.context?.emotion_tone_label ?? null,
          pacing_label: scene?.context?.pacing_label ?? seg.context?.pacing_label ?? null,
        });
      });
      const res = await postAudition(segments, from, to, { ...auditionOptions, labelsById, speakerMapping, idempotencyKey });
      if (requestId === context.auditionRequestRef.current) setAudition(res);
      auditionKeyRef.current = null;
      clearPendingIdempotencyKey("audition");
      setAuditionRetryPending(false);
    });
  }, [context.auditionRequestRef, run, segments, from, to, auditionOptions, setAudition, setError, sceneById, sceneBySegId]);

  const startNewAudition = useCallback(() => {
    auditionKeyRef.current = null;
    clearPendingIdempotencyKey("audition");
    setAuditionRetryPending(false);
  }, []);

  const runVisual = useCallback(async () => {
    const requestId = nextRequestId(context.visualRequestRef);
    await run("visual", async () => {
      try {
        const res = await postVisualLocalisation(segments);
        if (requestId === context.visualRequestRef.current) setVisual(res);
      } catch (error) {
        if (requestId === context.visualRequestRef.current) throw error;
      }
    });
  }, [context.visualRequestRef, run, segments, setVisual]);

  const runTranscribe = useCallback(async (audio: File) => {
    const requestId = nextRequestId(context.transcribeRequestRef);
    await run("transcribe", async () => {
      const res = await postTranscribe(audio);
      if (requestId !== context.transcribeRequestRef.current) return;
      loadSegments(res.segments.map((segment) => ({
        id: segment.id,
        text: segment.text,
        duration_seconds: segment.duration_seconds,
        speaker_id: segment.speaker ?? null,
        start_seconds: segment.start_seconds ?? null,
      })));
    });
  }, [context.transcribeRequestRef, run, loadSegments]);

  const runCopilot = useCallback(async (request: CopilotRequest, onProposal: (proposal: CopilotProposal) => void) => {
    const requestId = nextRequestId(context.copilotRequestRef);
    await run("copilot", async () => {
      const proposal = await postCopilotProposal(request);
      if (requestId === context.copilotRequestRef.current) onProposal(proposal);
    });
  }, [context.copilotRequestRef, run]);

  const runPipeline = useCallback(
    (audio: File | null, flags: { scene: boolean; audition: boolean }, speakerMapping: SpeakerMapping = {}, idempotencyKey?: string) =>
      run("pipeline", async () => {
        const requestId = nextRequestId(context.pipelineRequestRef);
        const priorById = new Map(segments.map((s) => [s.id, s] as const));
        const report = await postPipeline(audio, segments, segments, flags, from, to, { ...auditionOptions, speakerMapping }, idempotencyKey);
        if (requestId !== context.pipelineRequestRef.current) return;
        loadSegments(
          report.segments.map((s) => {
            const prior = audio ? undefined : priorById.get(s.id);
            return {
              ...(prior ?? {}),
              id: s.id,
              text: s.text,
              duration_seconds: s.duration_seconds,
              speaker_id: s.speaker ?? prior?.speaker_id ?? null,
              start_seconds: s.start_seconds ?? prior?.start_seconds ?? null,
            };
          }),
        );
        setPipeline(report);
        if (report.qa) setQa(report.qa);
        if (report.scene) setScene(report.scene);
        if (report.audition) setAudition(report.audition);
      }),
    [context.pipelineRequestRef, run, segments, from, to, auditionOptions, setPipeline, loadSegments, setQa, setScene, setAudition],
  );

  const runExportDub = useCallback(async (video: File) => {
    await run("export", async () => {
      const built = buildDubExportPlacements(segments, audition);
      if (built.problems.length > 0) {
        throw new Error(`Cannot export dubbed MP4: ${built.problems.join(" ")}`);
      }
      const placements = await Promise.all(built.placements.map(async (placement) => {
        const audio = placement.audio_data_url
          ? wavDataUrlToBlob(placement.audio_data_url)
          : await fetchAuditionWav(placement.audio_url!);
        return {
          id: placement.id,
          start_seconds: placement.start_seconds,
          audio,
          file_name: placement.file_name,
        };
      }));
      const blob = await postDubExport(video, segments.map((segment) => segment.id), placements);
      downloadBlob("trackdub-dub.mp4", blob);
    });
  }, [run, segments, audition]);

  return { busy, auditionRetryPending, runTranslate, runQa, runScene, runDirector, runAudition, startNewAudition, runVisual, runPipeline, runTranscribe, runCopilot, runExportDub };
}

async function fetchAuditionWav(url: string): Promise<Blob> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Audition audio download failed (${response.status}). The take may have expired.`);
  const audio = await response.blob();
  if (audio.size === 0) throw new Error("Audition audio download returned an empty file.");
  return audio;
}
