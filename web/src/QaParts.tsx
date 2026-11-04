import { type QaAttempt, type QaSegmentReport } from "./api";
import { fmtSec, verdictClass } from "./format";

export function TimingPill({ windowSec, estimated }: { windowSec: number | null; estimated: number | null }) {
  return (
    <span className="meta-pill" title="Timing window vs estimated duration">
      {windowSec != null ? `window ${fmtSec(windowSec)}` : "window —"}
      {estimated != null ? ` · est ${fmtSec(estimated)}` : ""}
    </span>
  );
}

function MeaningPill({ attempt, prefix, title, notCheckedTitle }: {
  attempt: QaAttempt;
  prefix: string;
  title: string;
  notCheckedTitle: string;
}) {
  if (attempt.meaning_verdict === "NotEvaluated") {
    return (
      <span className="verdict verdict-notevaluated" title={notCheckedTitle}>
        {`${prefix}Not checked`}
      </span>
    );
  }

  return (
    <span className={verdictClass(attempt.meaning_verdict)} title={title}>
      {`${prefix}${attempt.meaning_verdict}`}
    </span>
  );
}

export function MainMeaningPill({ attempt }: { attempt: QaAttempt }) {
  return (
    <MeaningPill
      attempt={attempt}
      prefix="Meaning: "
      title={`Meaning gate (attempt #${attempt.attempt_number})`}
      notCheckedTitle={`Not checked (timing failed, attempt #${attempt.attempt_number})`}
    />
  );
}

export function GatePills({ attempt, finalVerdict }: { attempt: QaAttempt; finalVerdict: string | null }) {
  return (
    <>
      <span className={verdictClass(attempt.timing_verdict)} title={`Timing gate (attempt #${attempt.attempt_number})`}>
        Timing: {attempt.timing_verdict}
      </span>
      <MainMeaningPill attempt={attempt} />
      <span className={verdictClass(finalVerdict ?? "idle")} title="Combined verdict">
        Final: {finalVerdict}
      </span>
    </>
  );
}

export function StatusPills({ q, shownAttempt, verdict, firstVerdict, translated }: {
  q: QaSegmentReport | undefined;
  shownAttempt: QaAttempt | null;
  verdict: string | null;
  firstVerdict: string | null;
  translated: string | null;
}) {
  if (q && shownAttempt) {
    return <GatePills attempt={shownAttempt} finalVerdict={verdict} />;
  }

  if (verdict) {
    return <span className={verdictClass(verdict)}>{verdict}</span>;
  }

  if (firstVerdict) {
    return <span className={verdictClass(firstVerdict)}>{firstVerdict}</span>;
  }

  if (translated) {
    return <span className="verdict verdict-pass">Translated</span>;
  }

  return <span className="verdict verdict-idle">Idle</span>;
}

export function RowMeaningPill({ attempt }: { attempt: QaAttempt }) {
  return (
    <MeaningPill
      attempt={attempt}
      prefix=""
      title={attempt.meaning_explanation ?? ""}
      notCheckedTitle="Not checked (timing failed)"
    />
  );
}

export function AttemptRow({ attempt }: { attempt: QaAttempt }) {
  return (
    <div className="attempt-row">
      <span>#{attempt.attempt_number}</span>
      <span>{attempt.kind}</span>
      <span className="attempt-text">{attempt.translation}</span>
      <span>
        {fmtSec(attempt.estimated_seconds)} / {fmtSec(attempt.window_seconds)} (max {attempt.max_chars}c)
      </span>
      <span className={verdictClass(attempt.timing_verdict)}>{attempt.timing_verdict}</span>
      <RowMeaningPill attempt={attempt} />
      <span className={verdictClass(attempt.verdict)}>{attempt.verdict}</span>
      <span>
        {attempt.total_tokens}
        {attempt.meaning_total_tokens > 0 ? ` +${attempt.meaning_total_tokens}m` : ""}
      </span>
    </div>
  );
}

export function MeaningIssueRow({ attempt }: { attempt: QaAttempt }) {
  const hasIssues = attempt.meaning_issues != null && attempt.meaning_issues.length > 0;
  if (!hasIssues && !attempt.meaning_explanation) {
    return null;
  }

  return (
    <div className="meaning-issues">
      <strong>#{attempt.attempt_number} {hasIssues ? "meaning issues:" : "meaning:"}</strong>{" "}
      {hasIssues ? attempt.meaning_issues!.join("; ") : attempt.meaning_explanation}
      {hasIssues && attempt.meaning_explanation ? ` — ${attempt.meaning_explanation}` : ""}
    </div>
  );
}

export function AttemptsFooter({ q, shownAttempt }: { q: QaSegmentReport; shownAttempt: QaAttempt | null }) {
  return (
    <>
      <div className="token-usage">
        Total tokens for line: {q.token_usage.total_tokens} (prompt {q.token_usage.prompt_tokens} + completion {q.token_usage.completion_tokens})
      </div>
      <div className="token-usage">
        Meaning gate (attempt #{shownAttempt?.attempt_number}): model {shownAttempt?.meaning_model_id ?? "—"} · template {shownAttempt?.meaning_prompt_template_version ?? "—"} ·{" "}
        {shownAttempt?.meaning_timestamp_utc ?? "—"}
      </div>
    </>
  );
}

export function AttemptsPanel({ q, shownAttempt }: { q: QaSegmentReport; shownAttempt: QaAttempt | null }) {
  const attempts = [q.first_attempt, ...q.revisions];
  return (
    <div className="attempts-panel">
      <div className="attempt-row attempt-row-header">
        <span>#</span>
        <span>Kind</span>
        <span>Translation</span>
        <span>Est / window</span>
        <span>Timing</span>
        <span>Meaning</span>
        <span>Verdict</span>
        <span>Tokens</span>
      </div>
      {attempts.map((a) => (
        <AttemptRow key={`${q.id}-${a.attempt_number}`} attempt={a} />
      ))}
      {attempts.map((a) => (
        <MeaningIssueRow key={`${q.id}-${a.attempt_number}-issue`} attempt={a} />
      ))}
      <AttemptsFooter q={q} shownAttempt={shownAttempt} />
    </div>
  );
}
