import {
  type DirectorAdaptationSegmentResult,
  type DirectorMeaningResult,
  type SegmentInput,
  type TranslationProvenance,
} from "./api";
import { fmtSec, verdictClass } from "./format";

export function DirectorStatusPill({ director }: { director: DirectorAdaptationSegmentResult }) {
  const statusClass = director.status === "Passed" ? "verdict-pass" : "verdict-fail";
  return (
    <span className={`verdict ${statusClass}`} title="Director adaptation (Passed only when timing passes and meaning is Pass)">
      Director: {director.status}
    </span>
  );
}

export function DirectorMeaningPill({ director }: { director: DirectorAdaptationSegmentResult }) {
  const meaning = director.meaning;
  if (!meaning) {
    return null;
  }

  if (meaning.verdict === "NotEvaluated") {
    return (
      <span className="verdict verdict-notevaluated" title="Not checked (timing failed)">
        Meaning: Not checked
      </span>
    );
  }

  return (
    <span className={verdictClass(meaning.verdict)} title={meaning.explanation}>
      Meaning: {meaning.verdict}
    </span>
  );
}

export function DirectorGatePills({ director }: { director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="line-meta">
      {director.timing_verdict && (
        <span className={verdictClass(director.timing_verdict)} title="Timing gate (adapted line)">
          Timing: {director.timing_verdict}
        </span>
      )}
      <DirectorMeaningPill director={director} />
      <DirectorStatusPill director={director} />
      {director.estimated_seconds != null && director.window_seconds != null && (
        <span className="meta-pill" title="Adapted estimate vs window">
          est {fmtSec(director.estimated_seconds)} / window {fmtSec(director.window_seconds)}
        </span>
      )}
    </div>
  );
}

export function DirectorSideBySide({ director }: { director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="director-grid">
      <div>
        <div className="director-label">Original translation</div>
        <div className="line-translation">{director.original_translation ?? "—"}</div>
      </div>
      <div>
        <div className="director-label">Adapted · {director.target_register ?? "—"}</div>
        <div className="line-translation">{director.adapted_translation ?? "—"}</div>
      </div>
    </div>
  );
}

function isQuietMeaning(meaning: DirectorMeaningResult): boolean {
  return meaning.issues.length === 0 && meaning.verdict === "Pass";
}

export function DirectorMeaningIssues({ director }: { director: DirectorAdaptationSegmentResult }) {
  const meaning = director.meaning;
  if (!meaning || isQuietMeaning(meaning)) {
    return null;
  }

  const hasIssues = meaning.issues.length > 0;
  return (
    <div className="meaning-issues">
      <strong>meaning{hasIssues ? " issues:" : ":"}</strong>{" "}
      {hasIssues ? meaning.issues.join("; ") : meaning.explanation}
      {hasIssues && meaning.explanation ? ` — ${meaning.explanation}` : ""}
    </div>
  );
}

export function DirectorErrorLine({ error }: { error: string }) {
  return (
    <div className="meaning-issues">
      <strong>Adaptation failed:</strong> {error}
    </div>
  );
}

export function DirectorChangeLine({ changed }: { changed: boolean }) {
  return <div className="token-usage">Adaptation: {changed ? "changed" : "no change"}</div>;
}

export function ContextFieldsLine({ fields }: { fields: string[] }) {
  return <div className="token-usage">Context fields used: {fields.length ? fields.join(", ") : "none"}</div>;
}

export function DirectorGateLine({ director, provenance }: { director: DirectorAdaptationSegmentResult; provenance: TranslationProvenance }) {
  const reasoning = director.usage?.reasoning_tokens;
  return (
    <div className="token-usage">
      Director gate: model {provenance.model_id} · template {provenance.prompt_template_version} · {provenance.timestamp_utc} · tokens{" "}
      {director.usage?.total_tokens ?? "—"}
      {reasoning != null ? ` (reasoning ${reasoning})` : ""}
      {director.meaning ? ` · meaning tokens ${director.meaning.usage.total_tokens}` : ""}
    </div>
  );
}

export function DirectorPanel({ seg, director }: { seg: SegmentInput; director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="attempts-panel" aria-label={`Director adaptation for ${seg.id}`}>
      <DirectorSideBySide director={director} />
      <DirectorGatePills director={director} />
      {director.error && <DirectorErrorLine error={director.error} />}
      <DirectorMeaningIssues director={director} />
      {director.changed != null && <DirectorChangeLine changed={director.changed} />}
      <ContextFieldsLine fields={director.context_fields_used} />
      {director.provenance && <DirectorGateLine director={director} provenance={director.provenance} />}
    </div>
  );
}
