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

  const sceneById = useMemo(() => (scene ? new Map(scene.segments.map((s, i) => [i, s] as const)) : null), [scene]);
  const sceneBySegId = useMemo(() => (scene ? new Map(scene.segments.map((s) => [s.id, s] as const)) : null), [scene]);
  const qaById = useMemo(() => (qa ? new Map(qa.segments.map((s) => [s.id, s] as const)) : null), [qa]);
  const batchById = useMemo(() => (batch ? new Map(batch.segments.map((s) => [s.id, s.translation] as const)) : null), [batch]);
  const directorById = useMemo(() => (director ? new Map(director.segments.map((s) => [s.id, s] as const)) : null), [director]);
  const auditionById = useMemo(() => (audition ? new Map(audition.segments.map((s) => [s.id, s] as const)) : null), [audition]);
  const visualById = useMemo(() => (visual ? new Map(visual.segments.map((s, i) => [i, s] as const)) : null), [visual]);

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
    let key = segmentKeysRef.current.get(segment);
    if (key === undefined) {
      key = ++segmentKeySequenceRef.current;
      segmentKeysRef.current.set(segment, key);
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
    segmentsRequestRef.current++;
    setSegments(next);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const replaceSegments = useCallback((next: SegmentInput[]) => {
    segmentsRequestRef.current++;
    setSegments((previous) => inheritSegmentKeys(previous, next));
    setError(null);
  }, [setError, inheritSegmentKeys]);

  const handleLoadSample = useCallback(() => {
    segmentsRequestRef.current++;
    setSegments(sampleSegments as SegmentInput[]);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const handleFile = useCallback(async (file: File) => {
    setError(null);
    const requestId = ++segmentsRequestRef.current;
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
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const f = e.target.files?.[0];
      if (f) void handleFile(f);
      if (fileInputRef.current) fileInputRef.current.value = "";
    },
    [handleFile],
  );

  const toggle = useCallback((id: string) => {
    setExpanded((m) => ({ ...m, [id]: !m[id] }));
  }, []);

  return { segments, expanded, fileInputRef, handleLoadSample, onFileChange, toggle, loadSegments, replaceSegments, keyForSegment };
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

  const qaOptions = qaOptionsFromSettings(settings);
  const auditionOptions = {
    qaOptions,
    speedRetake: speedRetakeFromSettings(settings),
    emotionTags: emotionTagsFromSettings(settings),
  };
  const runTranslate = useCallback(async () => {
    const requestId = ++context.translateRequestRef.current;
    await run("translate", async () => {
      const res = await postTranslate(segments, from, to);
      if (requestId === context.translateRequestRef.current) setBatch(res);
    });
  }, [context.translateRequestRef, run, segments, from, to, setBatch]);

  const runQa = useCallback(async () => {
    const requestId = ++context.qaRequestRef.current;
    await run("qa", async () => {
      const res = await postQa(segments, from, to, qaOptions);
      if (requestId === context.qaRequestRef.current) setQa(res);
    });
  }, [context.qaRequestRef, run, segments, from, to, qaOptions.maxAttempts, qaOptions.toleranceFraction, qaOptions.targetCharactersPerSecond, setQa]);

  const runScene = useCallback(async () => {
    const requestId = ++context.sceneRequestRef.current;
    await run("scene", async () => {
      const res = await postSceneContext(segments);
      if (requestId === context.sceneRequestRef.current) setScene(res);
    });
  }, [context.sceneRequestRef, run, segments, setScene]);

  const runDirector = useCallback(async () => {
    const requestId = ++context.directorRequestRef.current;
    await run("director", async () => {
      const lookups = { qaById, batchById, sceneById, sceneBySegId };
      const prepared = segments.map((seg, idx) => prepareDirectorInput(seg, idx, segments.length, lookups));
      const res = await postDirectorAdaptation(prepared, from, to, qaOptions);
      if (requestId === context.directorRequestRef.current) setDirector(res);
    });
  }, [context.directorRequestRef, run, segments, from, to, qaOptions.maxAttempts, qaOptions.toleranceFraction, qaOptions.targetCharactersPerSecond, setDirector, qaById, batchById, sceneById, sceneBySegId]);

  const runAudition = useCallback(async (speakerMapping: SpeakerMapping = {}) => {
    const requestId = ++context.auditionRequestRef.current;
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
  }, [context.auditionRequestRef, run, segments, from, to, settings, setAudition, sceneById, sceneBySegId]);

  const startNewAudition = useCallback(() => {
    auditionKeyRef.current = null;
    clearPendingIdempotencyKey("audition");
    setAuditionRetryPending(false);
  }, []);

  const runVisual = useCallback(async () => {
    const requestId = ++context.visualRequestRef.current;
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
    const requestId = ++context.transcribeRequestRef.current;
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
    const requestId = ++context.copilotRequestRef.current;
    await run("copilot", async () => {
      const proposal = await postCopilotProposal(request);
      if (requestId === context.copilotRequestRef.current) onProposal(proposal);
    });
  }, [context.copilotRequestRef, run]);

  const runPipeline = useCallback(
    (audio: File | null, flags: { scene: boolean; audition: boolean }, speakerMapping: SpeakerMapping = {}, idempotencyKey?: string) =>
      run("pipeline", async () => {
        const requestId = ++context.pipelineRequestRef.current;
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
    [context.pipelineRequestRef, run, segments, from, to, settings, setPipeline, loadSegments, setQa, setScene, setAudition],
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
