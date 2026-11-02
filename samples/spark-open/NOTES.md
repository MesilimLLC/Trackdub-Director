# Spark Open lane — Phase 2 notes

Runs executed 2026-11-02 (America/Los_Angeles) against the Azure Foundry
managed-compute deployment `meta-models--muse-spark-open-816b-a42b-nvfp4`
(GlobalManagedCompute, project `trackdub-6788`, resource
`trackdub-6788-resource`, westus3). Deployment live 07:56; translation run on a
fresh clock by Michael's decision at 09:27, stop 10:12, completed 09:41.

## Results

| Stage | Output | Result |
|---|---|---|
| Judge agreement (75 items) | judge-report.json | Compared 75/75, agree 73, disagree 2, excluded 0 — 97.3% agreement with hosted verdicts; 98,627 tokens |
| Production (6 voa10 lines, QA loop) | production-report.json | 6/6 final pass (5 first-pass, 1 revision), fully gate-verified; 20,009 tokens |
| Translation (40 labeled eval lines) | translation-report.json | 40/40 completed: timing pass 22/40 (55.0%), meaning pass 40/40 (100%), combined 22/40 (55.0%); Spark translation 63,016 tokens, hosted meaning 22,058 tokens |

Translation input (translation-input.json) was built from the 40 eval cases'
source lines only (id + text; no candidate, expected label, or error type was
included). Every translation was graded with the existing machinery only:
DurationEstimator timing, then MeaningGate.CheckMeaningAsync (hosted
meaning-v1, muse-spark-1.1). Side-by-side on the same 40 ids: the hosted lane's
recorded meaning-eval judged the seeded candidates (24 seeded errors caught,
0 missed, 0 false alarms over 16 clean); Spark Open's fresh translations of
the same source lines passed meaning 40/40 but timing only 22/40 — Spark's
lines preserve meaning and over-run the derived windows.

Call accounting against the 140 phase cap: 75 judge + 7 production + 1 billed
smoke + 40 translation = 123 Spark-side calls billed/counted (40 additional
hosted meaning-grading calls in translation). Judge 98,627 + production
20,009 + translation Spark 63,016 + billed smoke 592 = 182,244 Spark tokens;
hosted meaning grading in translation billed 22,058 tokens.

## Route facts (verified 2026-11-02)

- The .env API key does not authenticate this deployment: HTTP 404
  ("Resource not found") in both Bearer and api-key header forms, while an
  Entra bearer token succeeded at the same moment. Azure masks the bad-key
  case as 404 on this route; the long morning 404 stretch was this, not
  instance loading.
- Working auth: an in-process Entra bearer token minted per run via
  `az account get-access-token --resource https://ai.azure.com`, passed to
  the lane as its bearer credential in process memory only. It was never
  written to any file, report, or commit.
- Working base: the project-scoped URL
  `https://trackdub-6788-resource.services.ai.azure.com/api/projects/trackdub-6788/openai/v1`,
  used via in-process override of SPARKOPEN_BASE_URL. The root
  `/openai/v1` base from .env 404'd from the lane.
- Returned model string was exactly
  `meta-models--muse-spark-open-816b-a42b-nvfp4` on every successful call.
- Smoke is structurally incompatible with the lane handler and needs a
  builder fix: smoke asks for a `{"ok":true}` reply, and
  SparkOpenChatHandler rejects any final JSON object that is neither a
  meaning verdict nor a `{"translation":...}` object, surfacing HTTP 502
  (one billed attempt, 592 tokens). Judge bypasses that handler.
- Also fixed during the run: SparkOpenCli.ResolveFinalPath crashed with an
  unhandled FileNotFoundException when the --output report file did not yet
  exist; it now returns the unresolved path for a missing file.

## Verdict

Phase 2 complete, not partial: judge, production, and translation all ran to
completion inside their clocks and caps. Export of judge-report.json,
production-report.json, and translation-report.json in this folder is
confirmed; the Azure deployment can be deleted.
