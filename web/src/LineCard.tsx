import {
  type AuditionSegmentReport,
  type DirectorAdaptationSegmentResult,
  type QaSegmentReport,
  type SceneSegmentResult,
  type SegmentInput,
  type VisualLocalisationSegmentResult,
} from "./api";
import { AuditionPanel } from "./AuditionPanel";
import { DirectorPanel } from "./DirectorPanel";
import { type LineView, deriveLineView } from "./lineView";
import { AttemptsPanel, StatusPills, TimingPill } from "./QaParts";
import { ScenePanel, SceneStatusPill } from "./ScenePanel";
import { VisualPanel, VisualStatusPill } from "./VisualPanel";
import { resolveScene } from "./directorInput";

export interface LineCardProps {
  director: DirectorAdaptationSegmentResult | undefined;
  audition: AuditionSegmentReport | undefined;
  visual: VisualLocalisationSegmentResult | undefined;
  seg: SegmentInput;
  q: QaSegmentReport | undefined;
  translated: string | null;
  scene: SceneSegmentResult | undefined;
  isExpanded: boolean;
  onToggle: () => void;
}

export function LineMeta({ q, scene, view, translated, visual }: {
  q: QaSegmentReport | undefined;
  scene: SceneSegmentResult | undefined;
  view: LineView;
  translated: string | null;
  visual: VisualLocalisationSegmentResult | undefined;
}) {
  return (
    <div className="line-meta">
      <TimingPill windowSec={view.windowSec} estimated={view.estimated} />
      <StatusPills q={q} shownAttempt={view.shownAttempt} verdict={view.verdict} firstVerdict={view.firstVerdict} translated={translated} />
      <span className="attempts">Attempts: {view.attemptCount || "—"}</span>
      {scene && <SceneStatusPill scene={scene} />}
      {visual && <VisualStatusPill visual={visual} />}
      {q && q.revisions.length > 0 && <span className="revisions-count">Revisions: {q.revisions.length}</span>}
    </div>
  );
}

export function AttemptToggle({ isExpanded, onToggle }: { isExpanded: boolean; onToggle: () => void }) {
  return (
    <button type="button" className="expand-btn" onClick={onToggle} aria-expanded={isExpanded}>
      {isExpanded ? "Hide attempts" : "Show attempts"}
    </button>
  );
}

export function LineCard({ seg, q, translated, scene, director, audition, visual, isExpanded, onToggle }: LineCardProps) {
  const view = deriveLineView(seg, q, translated);

  return (
    <article key={seg.id} className="line-card" role="listitem">
      <div className="line-main">
        <div className="line-id">{seg.id}</div>
        <div className="line-source">{seg.text}</div>
        <div className="line-translation" title={view.displayTranslation}>
          {view.displayTranslation}
        </div>
        <LineMeta q={q} scene={scene} view={view} translated={translated} visual={visual} />
      </div>
      {q && <AttemptToggle isExpanded={isExpanded} onToggle={onToggle} />}
      {q && isExpanded && <AttemptsPanel q={q} shownAttempt={view.shownAttempt} />}
      {scene && <ScenePanel seg={seg} scene={scene} />}
      {director && director.status !== "NotRequested" && <DirectorPanel seg={seg} director={director} />}
      {audition && <AuditionPanel seg={seg} audition={audition} />}
      {visual && <VisualPanel seg={seg} visual={visual} />}
    </article>
  );
}

export interface LineListProps {
  segments: SegmentInput[];
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  sceneBySegId: Map<string, SceneSegmentResult> | null;
  directorById: Map<string, DirectorAdaptationSegmentResult> | null;
  auditionById: Map<string, AuditionSegmentReport> | null;
  visualById: Map<number, VisualLocalisationSegmentResult> | null;
  expanded: Record<string, boolean>;
  onToggle: (id: string) => void;
}

export function LineList({ segments, qaById, batchById, sceneById, sceneBySegId, directorById, auditionById, visualById, expanded, onToggle }: LineListProps) {
  return (
    <div className="lines" role="list">
      {segments.map((seg, idx) => (
        <LineCard
          key={idx}
          seg={seg}
          q={qaById?.get(seg.id)}
          translated={batchById?.get(seg.id) ?? null}
          scene={resolveScene(seg, idx, { sceneById, sceneBySegId }, segments.length)}
          director={directorById?.get(seg.id)}
          audition={auditionById?.get(seg.id)}
          visual={visualById?.get(idx)}
          isExpanded={expanded[seg.id] ?? false}
          onToggle={() => onToggle(seg.id)}
        />
      ))}
    </div>
  );
}
