# Roadmap -- Trackdub Director

All dates 2026. The pipeline is a sequence of verification lanes; each lane lands as a CLI subcommand and a server endpoint, with a committed report file as its evidence.

## Shipped (honest history, from README + metrics snapshot)

- **Oct 26** -- Core + Meta Model API smoke test; translation lane with provenance and usage; QA/timing loop with reasoning_effort control; local web app (ASP.NET Core server + React frontend).
- **Oct 28** -- Meaning gate after the timing check; Director Stage A scene context via SAM 3.1; demo asset spec and metrics definitions.
- **Oct 29** -- Stage B1 vision enrichment; Stage B2 register adaptation with gate re-verification; CI for .NET and web.
- **Oct 30** -- VOA Lesson 10 dataset; audition lane (voice casting with measured, gated multi-take selection via ElevenLabs TTS).
- **Oct 31** -- Visual localisation (crop-edit-composite with Muse Image); audition alternates + speed variant; uniform-config scale reports; prebuilt web bundle; static-shot gate for video-rate sign localisation; meaning-gate measurement (70% pressure run + meaning-eval over 40 labeled cases).

## Current state (Nov 2 freeze)

- 259 .NET test cases discovered (261 executed, 0 failed), 11 web tests.
- CLI subcommands present: smoke, translate, models, qa, sign-shot, meaning-eval, verify-only, plus newer ingest / pipeline / recover.
- Verify-only receipt lane: zero-network CI gate that recomputes every verdict from recorded numbers and hash-seals the manifest + media.
- Cold start median 3.04 s. End-to-end wall time: NotMeasured.

## In flight (hard deadline)

- **Spark Open lane (feat/spark-open-lane)** -- adds Meta's open-weight Muse Spark Open as a selectable, measured-only provider for the text lanes: (1) independent meaning judge over already-judged candidates (answers "Spark grades Spark"), (2) translation provider for the six voa10 demo lines through the QA loop, (3) translation provider over the 40-case labeled eval. Outputs under samples/spark-open/ only; never changes a shipped verdict or default. **Hard stop: lands by Nov 2 EOD or it does not land.** Phase 1 (offline build + tests, zero live calls) then Phase 2 (metered Azure runs, caps enforced), gated on Michael provisioning the Azure deployment.

## Remaining to submission (Nov 2 -> Nov 4 9PM ET)

1. Land or cleanly drop the Spark Open lane per its hard stop.
2. Measure end-to-end wall time (currently the only NotMeasured headline metric).
3. Freeze the metrics snapshot against final merged main; re-add every token total.
4. Record the 3:00 demo video to the storyboard; all numbers reproducible from repo reports; disclosure + credit cards verbatim.
5. Final submission package: public repo, video link, team list, via Cerebral Valley before the deadline.

## Out of scope

Lip-sync, live/streaming dub, dollar-cost claims, SAM mask-accuracy scoring, any figure from the separate Trackdub AWS POC.
