import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  type AuditionReport,
  type DirectorAdaptationBatch,
  type PipelineReport,
  type QaReport,
  type QaSegmentReport,
  type SceneContextBatch,
  type SceneSegmentResult,
  type SegmentInput,
  type StatusResponse,
  type TranslateBatch,
  type VisualLocalisationBatch,
  fetchStatus,
  postAudition,
  postDirectorAdaptation,
  postPipeline,
  postQa,
  postSceneContext,
  postTranslate,
  postVisualLocalisation,
} from "./api";
import sampleSegments from "./sampleSegments.json";
import { prepareDirectorInput } from "./directorInput";
import { errorMessage, parseSegments } from "./format";

export const DEFAULT_FROM = "en";

export const DEFAULT_TO = "es";

export type BusyState = "translate" | "qa" | "scene" | "director" | "audition" | "visual" | "pipeline" | null;

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
    void refreshStatus();
  }, [refreshStatus]);

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

  const reset = useCallback(() => {
    setQa(null);
    setBatch(null);
    setScene(null);
    setDirector(null);
    setAudition(null);
    setVisual(null);
    setPipeline(null);
  }, []);
  const sceneById = useMemo(() => (scene ? new Map(scene.segments.map((s, i) => [i, s] as const)) : null), [scene]);
  const sceneBySegId = useMemo(() => (scene ? new Map(scene.segments.map((s) => [s.id, s] as const)) : null), [scene]);
  const qaById = useMemo(() => (qa ? new Map(qa.segments.map((s) => [s.id, s] as const)) : null), [qa]);
  const batchById = useMemo(() => (batch ? new Map(batch.segments.map((s) => [s.id, s.translation] as const)) : null), [batch]);

  const directorById = useMemo(() => (director ? new Map(director.segments.map((s) => [s.id, s] as const)) : null), [director]);
  const auditionById = useMemo(() => (audition ? new Map(audition.segments.map((s) => [s.id, s] as const)) : null), [audition]);
  const visualById = useMemo(() => (visual ? new Map(visual.segments.map((s, i) => [i, s] as const)) : null), [visual]);

  return { qa, setQa, batch, setBatch, scene, setScene, director, setDirector, audition, setAudition, visual, setVisual, pipeline, setPipeline, reset, qaById, sceneById, sceneBySegId, batchById, directorById, auditionById, visualById };
}

export function useSegments(onReset: () => void, setError: React.Dispatch<React.SetStateAction<string | null>>) {
  const [segments, setSegments] = useState<SegmentInput[]>(() => sampleSegments as SegmentInput[]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);

  const loadSegments = useCallback((next: SegmentInput[]) => {
    setSegments(next);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const handleLoadSample = useCallback(() => {
    setSegments(sampleSegments as SegmentInput[]);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const handleFile = useCallback(async (file: File) => {
    setError(null);
    try {
      setSegments(parseSegments(await file.text()));
      onReset();
    } catch (e) {
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

  return { segments, expanded, fileInputRef, handleLoadSample, onFileChange, toggle, loadSegments };
}

export interface ActionsContext {
  segments: SegmentInput[];
  from: string;
  to: string;
  refreshStatus: () => Promise<void>;
  setBatch: (batch: TranslateBatch) => void;
  setQa: (qa: QaReport) => void;
  setScene: (scene: SceneContextBatch) => void;
  setDirector: (director: DirectorAdaptationBatch) => void;
  setAudition: (audition: AuditionReport) => void;
  setVisual: (visual: VisualLocalisationBatch) => void;
  setPipeline: (report: PipelineReport) => void;
  loadSegments: (segments: SegmentInput[]) => void;
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  sceneBySegId: Map<string, SceneSegmentResult> | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  sceneRequestRef: React.MutableRefObject<number>;
  directorRequestRef: React.MutableRefObject<number>;
  auditionRequestRef: React.MutableRefObject<number>;
  visualRequestRef: React.MutableRefObject<number>;
  pipelineRequestRef: React.MutableRefObject<number>;
}

export function useActions({ segments, from, to, refreshStatus, setBatch, setQa, setScene, setDirector, setAudition, setVisual, setPipeline, loadSegments, qaById, batchById, sceneById, sceneBySegId, setError, sceneRequestRef, directorRequestRef, auditionRequestRef, visualRequestRef, pipelineRequestRef }: ActionsContext) {
  const [busy, setBusy] = useState<BusyState>(null);

  const run = useCallback(async (kind: Exclude<BusyState, null>, action: () => Promise<void>) => {
    setBusy(kind);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
      void refreshStatus();
    }
  }, [refreshStatus, setError]);

  const runTranslate = useCallback(
    () => run("translate", async () => setBatch(await postTranslate(segments, from, to))),
    [run, segments, from, to, setBatch],
  );
  const runQa = useCallback(
    () => run("qa", async () => setQa(await postQa(segments, from, to))),
    [run, segments, from, to, setQa],
  );
  const runScene = useCallback(async () => {
    const requestId = ++sceneRequestRef.current;
    await run("scene", async () => {
      const res = await postSceneContext(segments);
      if (requestId === sceneRequestRef.current) setScene(res);
    });
  }, [run, segments, setScene, sceneRequestRef]);

  const runDirector = useCallback(async () => {
    const requestId = ++directorRequestRef.current;
    await run("director", async () => {
      const lookups = { qaById, batchById, sceneById, sceneBySegId };
      const prepared = segments.map((seg, idx) => prepareDirectorInput(seg, idx, segments.length, lookups));
      const res = await postDirectorAdaptation(prepared, from, to);
      if (requestId === directorRequestRef.current) setDirector(res);
    });
  }, [run, segments, from, to, setDirector, qaById, batchById, sceneById, sceneBySegId, directorRequestRef]);

  const runAudition = useCallback(async () => {
    const requestId = ++auditionRequestRef.current;
    await run("audition", async () => {
      const res = await postAudition(segments, from, to);
      if (requestId === auditionRequestRef.current) setAudition(res);
    });
  }, [run, segments, from, to, setAudition, auditionRequestRef]);

  const runVisual = useCallback(async () => {
    const requestId = ++visualRequestRef.current;
    await run("visual", async () => {
      try {
        const res = await postVisualLocalisation(segments);
        if (requestId === visualRequestRef.current) setVisual(res);
      } catch (error) {
        if (requestId === visualRequestRef.current) throw error;
      }
    });
  }, [run, segments, setVisual, visualRequestRef]);

  const runPipeline = useCallback(
    (audio: File | null, flags: { scene: boolean; audition: boolean }) =>
      run("pipeline", async () => {
        const requestId = ++pipelineRequestRef.current;
        const priorById = new Map(segments.map((s) => [s.id, s] as const));
        const report = await postPipeline(audio, segments, segments, flags, from, to);
        if (requestId !== pipelineRequestRef.current) {
          return;
        }
        // Audio runs replace the working set with ASR output. Segment-only runs
        // keep frames / concepts / director opt-ins and only refresh transcript fields.
        loadSegments(
          report.segments.map((s) => {
            const prior = audio ? undefined : priorById.get(s.id);
            return {
              ...(prior ?? {}),
              id: s.id,
              text: s.text,
              duration_seconds: s.duration_seconds,
              speaker_id: s.speaker ?? prior?.speaker_id ?? null,
            };
          }),
        );
        setPipeline(report);
        if (report.qa) setQa(report.qa);
        if (report.scene) setScene(report.scene);
        if (report.audition) setAudition(report.audition);
      }),
    [run, segments, from, to, setPipeline, loadSegments, setQa, setScene, setAudition, pipelineRequestRef],
  );

  return { busy, runTranslate, runQa, runScene, runDirector, runAudition, runVisual, runPipeline };
}
