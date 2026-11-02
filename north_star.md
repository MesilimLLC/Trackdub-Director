# North Star -- Trackdub Director

## The one line

A dubbing workbench that can say no, and shows its work. Verification gates sit between every pipeline stage and record a verdict -- with provenance and token cost -- for every line and frame. Built on the Meta Model API for the Meta AI Developer Hackathon (Oct 26 - Nov 4, 2026).

## Why it matters

AI dubbing fails quietly: a translated line runs long, drops the subject, or an on-screen sign stays in English, and nothing downstream checks. Studios still pay people to catch this by ear. Trackdub Director is that check, as software. The product thesis is not "translate better" -- it is "refuse honestly and prove it."

## Definition of done (submission)

A public repo and a 3:00 demo video, submitted via Cerebral Valley by **Nov 4, 9:00 PM ET**, that a judge can run and verify, scoring well on the four equally-weighted Stage Two criteria:

- **Impact** -- a real problem (quiet dubbing failures) with a real check.
- **Use of Meta AI tooling** -- Muse Spark, SAM 3.1, Muse Image, Muse Voice Transcribe used beyond basic integration; surface surprising behavior (the SAM reflection trap, the full-frame regeneration failure).
- **Technical depth & execution** -- sound engineering, every claim backed by a committed report file.
- **Demo** -- a working, reproducible demo video; every on-screen number comes from a repo report.

## Invariants (never violate)

- API keys are read from the environment only, never from files, never logged.
- Only work built during the hackathon appears in the repo or the demo. The author's pre-existing Trackdub product is a pipeline-stage boundary, never copied or called.
- Pre-existing material is disclosed: VOA Learning English footage (public domain) and ElevenLabs stock voices.
- No "Llama" wording anywhere. Models are Muse Spark, SAM 3.1, Muse Image, Muse Voice Transcribe.
- A skipped check never reads as a pass. NotEvaluated is a review reason, not a pass.
- Every metric names the report file it came from. Nothing is estimated; "not measured" is stated with its reason. No dollar costs -- tokens are the cost metric.
- No greenfield scope creep: pipeline stages only, with one real feedback loop (the QA/timing bounce-back). No live/streaming dub, no lip-sync.

## Scope guardrails

Anti-project list to avoid (per the participant guide): medical advice, basic RAG, simple image analyzers, education chatbots, job screeners, anything political or war-related. Trackdub Director is a localisation-QA tool and sits clear of all of these.
