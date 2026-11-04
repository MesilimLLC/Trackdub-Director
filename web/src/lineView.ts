import { type QaAttempt, type QaSegmentReport, type SegmentInput } from "./api";

export interface LineView {
  displayTranslation: string;
  windowSec: number | null;
  verdict: string | null;
  firstVerdict: string | null;
  shownAttempt: QaAttempt | null;
  estimated: number | null;
  attemptCount: number;
}

function deriveShownAttempt(q: QaSegmentReport | undefined): QaAttempt | null {
  if (!q) {
    return null;
  }

  return q.revisions.length > 0 ? q.revisions[q.revisions.length - 1] : q.first_attempt;
}

function deriveAttemptCount(q: QaSegmentReport | undefined, translated: string | null): number {
  if (q) {
    return 1 + q.revisions.length;
  }

  return translated ? 1 : 0;
}

export function deriveLineView(seg: SegmentInput, q: QaSegmentReport | undefined, translated: string | null): LineView {
  // Gate pills and the estimate must describe the translation shown (the final attempt),
  // not the initial one; otherwise a successful bounce-back displays failing gates.
  const shownAttempt = deriveShownAttempt(q);
  return {
    displayTranslation: q?.final_translation ?? translated ?? "—",
    windowSec: q?.window_seconds ?? seg.duration_seconds ?? null,
    verdict: q?.final_verdict ?? null,
    firstVerdict: q?.first_attempt.verdict ?? null,
    shownAttempt,
    estimated: shownAttempt?.estimated_seconds ?? null,
    attemptCount: deriveAttemptCount(q, translated),
  };
}
