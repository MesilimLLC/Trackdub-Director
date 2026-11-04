import { useCallback, useRef, useState } from "react";
import { AppFooter, AppHeader, Toolbar } from "./AppChrome";
import { AuditionBudgetNotice } from "./AuditionPanel";
import { LineList } from "./LineCard";
import { PipelinePanel } from "./PipelinePanel";
import { QaReportFooter } from "./QaReport";
import { DEFAULT_FROM, DEFAULT_TO, useActions, useReports, useSegments, useStatus } from "./hooks";

export default function App() {
  const [error, setError] = useState<string | null>(null);
  const { status, statusError, refreshStatus } = useStatus();
  const reports = useReports();
  const sceneRequestRef = useRef(0);
  const directorRequestRef = useRef(0);
  const auditionRequestRef = useRef(0);
  const visualRequestRef = useRef(0);
  const pipelineRequestRef = useRef(0);
  const [resetGeneration, setResetGeneration] = useState(0);
  const onReset = useCallback(() => {
    reports.reset();
    sceneRequestRef.current += 1;
    directorRequestRef.current += 1;
    auditionRequestRef.current += 1;
    visualRequestRef.current += 1;
    pipelineRequestRef.current += 1;
    setResetGeneration((n) => n + 1);
  }, [reports]);
  const input = useSegments(onReset, setError);
  const actions = useActions({
    segments: input.segments,
    from: DEFAULT_FROM,
    to: DEFAULT_TO,
    refreshStatus,
    setBatch: reports.setBatch,
    setQa: reports.setQa,
    setScene: reports.setScene,
    setDirector: reports.setDirector,
    setAudition: reports.setAudition,
    setVisual: reports.setVisual,
    setPipeline: reports.setPipeline,
    loadSegments: input.loadSegments,
    qaById: reports.qaById,
    batchById: reports.batchById,
    sceneById: reports.sceneById,
    sceneBySegId: reports.sceneBySegId,
    setError,
    sceneRequestRef,
    directorRequestRef,
    auditionRequestRef,
    visualRequestRef,
    pipelineRequestRef,
  });

  return (
    <div className="app">
      <AppHeader status={status} statusError={statusError} error={error} qa={reports.qa} />
      <main className="main">
        <PipelinePanel
          busy={actions.busy}
          pipeline={reports.pipeline}
          resetGeneration={resetGeneration}
          onRun={actions.runPipeline}
        />
        <Toolbar
          segments={input.segments}
          from={DEFAULT_FROM}
          to={DEFAULT_TO}
          busy={actions.busy}
          fileInputRef={input.fileInputRef}
          onFileChange={input.onFileChange}
          onLoadSample={input.handleLoadSample}
          onTranslate={actions.runTranslate}
          onQa={actions.runQa}
          onScene={actions.runScene}
          onDirector={actions.runDirector}
          onAudition={actions.runAudition}
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
