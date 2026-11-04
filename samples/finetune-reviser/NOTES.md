# Timing-fit reviser dataset — build result (2026-11-04)

Verdict up front: **there is no leakage-clean training set in the captured
reports.** `train.jsonl` and `holdout.jsonl` are empty, correctly. This file
explains why, so nobody trains on these reports by accident later.

## Method

Candidate pool (11 files, listed in `manifest.json`): root `qa-report.json`,
`samples/voa10-scale/qa-report-*.json`, `samples/voa10-full/qa-report-*.json`,
and `samples/voa10/pipeline-run/*.json`. An example would be one failed QA
attempt (source line, window, failed candidate, timing/meaning verdicts and
recorded meaning explanation) paired with the accepted final translation for
the same segment.

Hard rules applied before any pair could enter:

- **Frozen sources excluded by source text.** Any segment whose English line
  appears in the Spark Open translation eval, the Spark Open judge set, the
  meaning-eval pressure set, or the human-review blind pack is excluded
  entirely. Those four pools turn out to draw on the same 20 distinct source
  lines (16 of them in all four) — the benchmarks are one shared line set
  wearing four hats.
- **Whole pools excluded:** `samples/meaning-eval/**`, `samples/spark-open/**`,
  `samples/human-review/**`, `samples/glimmer-recovery/**`, and audition takes
  (v1 scope is text-QA attempts only).
- **Telegraphic screen:** accepted targets with known clipped forms
  (`mañ`, `mñ`), vowel-less tokens, ≤2 words for a ≥6-word source,
  compression ratio under 0.35, a dropped proper name, or a dropped question
  mark would be excluded and listed in `excluded.json`. No pair survived long
  enough to reach this screen.
- **Dedupe** on (source, window, failed candidate, accepted candidate), and
  **split by source line** (SHA-256 of normalized source, first byte mod 5),
  never by example.

## Counts

- 94 segments / 108 attempts seen, 33 distinct source lines.
- 72 segments excluded as frozen sources; the remaining 22 segments all
  passed on the first attempt — no failed attempt exists to pair.
- Ignoring the freeze entirely, the whole pool contains only **14 failed
  attempts with an accepted final, over 6 distinct source lines — and all 6
  lines are in every frozen pool.** Training on them would contaminate the
  judge agreement, translation eval, and blind pack numbers simultaneously.

The failure data was generated almost exclusively on lines that were then
reused as benchmarks. That is the finding: the corpus, as captured, cannot
teach timing-fit revision without eating its own evidence.

## What would create a real dataset

1. **Generate fresh lines** (new English dialogue, disjoint from the frozen
   20) and run the existing hosted QA loop at deliberately tight windows to
   harvest failed→accepted pairs. Gates label them; a human or the blind-pack
   process naturalness-filters the accepted side. This is hosted-call spend,
   not training spend, and needs its own go.
2. **Or retire one benchmark on purpose** — e.g. after the blind pack is
   graded, declare it training data and build a new holdout. That is a
   product decision, not a builder default.

Until one of those happens, do not fine-tune anything on the captured QA
reports.
