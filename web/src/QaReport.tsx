import {
  type QaReport,
  type QaTotals,
} from "./api";
import { pct } from "./format";

export function QaStats({ totals }: { totals: QaTotals }) {
  return (
    <>
      <div className="qa-stat">
        <span className="qa-stat-label">First-pass</span>
        <span className="qa-stat-value">{pct(totals.first_pass_rate)}</span>
        <span className="qa-stat-detail">
          {totals.first_pass_count}/{totals.total_segments}
        </span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Final</span>
        <span className="qa-stat-value qa-stat-final">{pct(totals.final_pass_rate)}</span>
        <span className="qa-stat-detail">
          {totals.final_pass_count}/{totals.total_segments}
        </span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Revisions</span>
        <span className="qa-stat-value">{totals.total_revisions}</span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Tokens</span>
        <span className="qa-stat-value">{totals.total_tokens.toLocaleString()}</span>
      </div>
    </>
  );
}

export function QaSummary({ qa }: { qa: QaReport | null }) {
  return (
    <div className="qa-summary" aria-label="QA summary">
      {qa ? <QaStats totals={qa.totals} /> : <span className="qa-empty">Run QA to see pass rates</span>}
    </div>
  );
}

export function QaReportFooter({ qa }: { qa: QaReport }) {
  return (
    <section className="report-footer">
      <h2>QA report</h2>
      <pre className="report-json">{JSON.stringify(qa.totals, null, 2)}</pre>
      <p className="report-note">
        Shape matches <code>samples/qa-report.json</code> from <code>dotnet run --project src/Trackdub.Director.Cli -- qa</code>.
      </p>
    </section>
  );
}
