import { useCallback, useEffect, useRef, useState } from "react";
import { AppFooter, AppHeader, Toolbar } from "./AppChrome";
import { AuditionBudgetNotice } from "./AuditionPanel";
import { LineList } from "./LineCard";
import { PipelinePanel } from "./PipelinePanel";
import { SegmentMediaEditor } from "./SegmentMediaEditor";
import { QaReportFooter } from "./QaReport";
import { CopilotPanel, DubExportPanel, RunSettingsPanel, SegmentEditor } from "./WorkbenchPanels";
import type { CopilotProposal, SegmentInput, SpeakerMapping } from "./api";
import { buildCopilotRequest, planCopilotApply } from "./copilot";
import { DEFAULT_FROM, DEFAULT_TO, useActions, useReports, useSegments, useStatus } from "./hooks";
import {
  DEFAULT_RUN_SETTINGS,
  type ChangeKind,
  type ReportKind,
  type RunSettings,
  classifySegmentChanges,
  downloadJson,
  reportsForChanges,
  stripAudioTransport,
} from "./workbench";

export default function App() {
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState(DEFAULT_FROM);
  const [to, setTo] = useState(DEFAULT_TO);
  const [settings, setSettings] = useState<RunSettings>(DEFAULT_RUN_SETTINGS);
  const [speakerMapping, setSpeakerMapping] = useState<SpeakerMapping>({ A: "anna", B: "ashley" });
  const [selectedIndexState, setSelectedIndex] = useState(0);
  const [copilotGoal, setCopilotGoal] = useState("");
  const [copilotProposal, setCopilotProposal] = useState<{ proposal: CopilotProposal; fingerprint: string } | null>(null);
  const { status, statusError, refreshStatus } = useStatus();
  const reports = useReports();
  const translateRequestRef = useRef(0);
  const qaRequestRef = useRef(0);
  const sceneRequestRef = useRef(0);
  const directorRequestRef = useRef(0);
  const auditionRequestRef = useRef(0);
  const visualRequestRef = useRef(0);
  const pipelineRequestRef = useRef(0);
  const transcribeRequestRef = useRef(0);
  const copilotRequestRef = useRef(0);
  const [resetGeneration, setResetGeneration] = useState(0);

  const invalidateReports = useCallback((kinds: Iterable<ReportKind>) => {
    copilotRequestRef.current++;
    setCopilotProposal(null);
    const unique = [...new Set(kinds)];
    reports.clearKinds(unique);
    const requestRefs: Partial<Record<ReportKind, { current: number }>> = {
      batch: translateRequestRef, qa: qaRequestRef, scene: sceneRequestRef,
      director: directorRequestRef, audition: auditionRequestRef,
      visual: visualRequestRef, pipeline: pipelineRequestRef,
    };
    for (const kind of unique) {
      const requestRef = requestRefs[kind];
      if (requestRef) requestRef.current++;
    }
  }, [reports]);

  const invalidateChanges = useCallback((changes: Iterable<ChangeKind>) => {
    invalidateReports(reportsForChanges(changes));
  }, [invalidateReports]);

  const onReset = useCallback(() => {
    reports.reset();
    translateRequestRef.current += 1;
    qaRequestRef.current += 1;
    sceneRequestRef.current += 1;
    directorRequestRef.current += 1;
    auditionRequestRef.current += 1;
    visualRequestRef.current += 1;
    pipelineRequestRef.current += 1;
    transcribeRequestRef.current += 1;
    copilotRequestRef.current += 1;
    setCopilotProposal(null);
    setResetGeneration((n) => n + 1);
  }, [reports]);

  const input = useSegments(onReset, setError);
  const segmentsRef = useRef(input.segments);
  useEffect(() => {
    segmentsRef.current = input.segments;
  });
  const selectedIndex = Math.max(0, Math.min(selectedIndexState, Math.max(0, input.segments.length - 1)));

  const updateSegments = useCallback((next: SegmentInput[]) => {
    invalidateChanges(classifySegmentChanges(input.segments, next));
    input.replaceSegments(next);
  }, [input, invalidateChanges]);

  const updateSegment = useCallback((next: SegmentInput) => {
    updateSegments(input.segments.map((segment, index) => (index === selectedIndex ? next : segment)));
  }, [input.segments, selectedIndex, updateSegments]);

  const updateSegmentFrame = useCallback((expected: SegmentInput, patch: Partial<SegmentInput>): boolean => {
    const key = input.keyForSegment(expected);
    const current = segmentsRef.current;
    const index = current.findIndex((candidate) => input.keyForSegment(candidate) === key);
    if (index < 0) return false;
    const next = current.map((segment, i) => (i === index ? { ...segment, ...patch } : segment));
    invalidateChanges(classifySegmentChanges(current, next));
    input.replaceSegments(next);
    return true;
  }, [input, invalidateChanges]);

  const updateFrom = useCallback((value: string) => {
    invalidateChanges(["language"]);
    setFrom(value);
  }, [invalidateChanges]);

  const updateTo = useCallback((value: string) => {
    invalidateChanges(["language"]);
    setTo(value);
  }, [invalidateChanges]);

  const updateSettings = useCallback((next: RunSettings, change: ChangeKind) => {
    invalidateChanges([change]);
    setSettings(next);
  }, [invalidateChanges]);

  const actions = useActions({
    segments: input.segments,
    from,
    to,
    settings,
    refreshStatus,
    setBatch: reports.setBatch,
    setQa: reports.setQa,
    setScene: reports.setScene,
    setDirector: reports.setDirector,
    setAudition: reports.setAudition,
    setVisual: reports.setVisual,
    setPipeline: reports.setPipeline,
    loadSegments: input.loadSegments,
    audition: reports.audition,
    qaById: reports.qaById,
    batchById: reports.batchById,
    sceneById: reports.sceneById,
    sceneBySegId: reports.sceneBySegId,
    setError,
    translateRequestRef,
    qaRequestRef,
    sceneRequestRef,
    directorRequestRef,
    auditionRequestRef,
    visualRequestRef,
    pipelineRequestRef,
    transcribeRequestRef,
    copilotRequestRef,
  });

  const evidence = stripAudioTransport({
    segments: input.segments,
    languages: { from, to },
    settings,
    translate: reports.batch,
    qa: reports.qa,
    scene: reports.scene,
    director: reports.director,
    audition: reports.audition,
    visual: reports.visual,
    pipeline: reports.pipeline,
  });
  const evidenceAvailable = Boolean(reports.batch || reports.qa || reports.scene || reports.director || reports.audition || reports.visual || reports.pipeline);

  const currentCopilotRequest = () => buildCopilotRequest({
    goal: copilotGoal,
    from: from || DEFAULT_FROM,
    to: to || DEFAULT_TO,
    settings,
    segments: input.segments,
    reports,
  });

  const prepareCopilot = () => {
    const request = currentCopilotRequest();
    const fingerprint = JSON.stringify(request);
    void actions.runCopilot(request, (proposal) => setCopilotProposal({ proposal, fingerprint }));
  };

  const applyCopilot = () => {
    const plan = planCopilotApply({
      proposalState: copilotProposal,
      currentFingerprint: JSON.stringify(currentCopilotRequest()),
      segments: input.segments,
      settings,
    });
    if ("error" in plan) {
      if (plan.clearProposal) setCopilotProposal(null);
      setError(plan.error);
      return;
    }
    if (plan.merged.changes.length > 0) {
      invalidateChanges(plan.merged.changes);
      setSettings(plan.merged.settings);
    }
    if (plan.segments) updateSegments(plan.segments);
    setCopilotProposal(null);
  };

  return (
    <div className="app">
      <AppHeader status={status} statusError={statusError} error={error} qa={reports.qa} />
      <main className="main">
        <RunSettingsPanel
          from={from}
          to={to}
          settings={settings}
          segments={input.segments}
          busy={actions.busy !== null}
          evidenceAvailable={evidenceAvailable}
          onFromChange={updateFrom}
          onToChange={updateTo}
          onSettingsChange={updateSettings}
          onDownloadInput={() => downloadJson("director-segments.json", input.segments)}
          onDownloadEvidence={() => downloadJson("director-evidence.json", evidence)}
        />
        <SegmentEditor
          segments={input.segments}
          busy={actions.busy !== null}
          selectedIndex={selectedIndex}
          onSelect={setSelectedIndex}
          onChange={updateSegments}
        />
        <CopilotPanel
          goal={copilotGoal}
          proposal={copilotProposal?.proposal ?? null}
          busy={actions.busy === "copilot"}
          disabled={actions.busy !== null}
          onGoalChange={(value) => { setCopilotGoal(value); setCopilotProposal(null); copilotRequestRef.current++; }}
          onPrepare={prepareCopilot}
          onApply={applyCopilot}
          onDiscard={() => setCopilotProposal(null)}
        />
        <SegmentMediaEditor
          segment={input.segments[selectedIndex]}
          busy={actions.busy !== null}
          onChange={updateSegment}
          onFrameLoaded={updateSegmentFrame}
          onError={setError}
        />
        <PipelinePanel
          busy={actions.busy}
          pipeline={reports.pipeline}
          resetGeneration={resetGeneration}
          segments={input.segments}
          speakerMapping={speakerMapping}
          onSpeakerMappingChange={(mapping) => {
            invalidateChanges(["auditionOptions"]);
            setSpeakerMapping(mapping);
          }}
          onRun={actions.runPipeline}
          onTranscribe={actions.runTranscribe}
          onAudioChange={() => invalidateChanges(["audio"])}
          onRecoveryError={setError}
        />
        <DubExportPanel
          segments={input.segments}
          audition={reports.audition}
          busy={actions.busy !== null}
          exporting={actions.busy === "export"}
          resetGeneration={resetGeneration}
          onExport={actions.runExportDub}
        />
        <Toolbar
          segments={input.segments}
          from={from}
          to={to}
          busy={actions.busy}
          fileInputRef={input.fileInputRef}
          onFileChange={input.onFileChange}
          onLoadSample={input.handleLoadSample}
          onTranslate={actions.runTranslate}
          onQa={actions.runQa}
          onScene={actions.runScene}
          onDirector={actions.runDirector}
          onAudition={() => actions.runAudition(speakerMapping)}
          auditionRetryPending={actions.auditionRetryPending}
          onNewAudition={actions.startNewAudition}
          onVisual={actions.runVisual}
        />
        {reports.audition && <AuditionBudgetNotice totals={reports.audition.totals} />}
        <LineList
          segments={input.segments}
          qaById={reports.qaById}
          batchById={reports.batchById}
          sceneById={reports.sceneById}
          sceneBySegId={reports.sceneBySegId}
          directorById={reports.directorById}
          auditionById={reports.auditionById}
          visualById={reports.visualById}
          expanded={input.expanded}
          onToggle={input.toggle}
        />
        {reports.qa && <QaReportFooter qa={reports.qa} />}
      </main>
      <AppFooter />
    </div>
  );
}
