# Tasks -- Trackdub Director

Ordered by leverage toward the Nov 4 9PM ET submission. Check off as completed; keep this file current.

## P0 -- deadline-critical

- [ ] **Spark Open lane** (feat/spark-open-lane): resolve by Nov 2 EOD per its hard stop. Phase 1 offline build + tests must be green and "ready to call" before any metered Azure run. If not ready in time, cleanly drop the branch rather than ship a half-lane. Outputs under samples/spark-open/ only; no shipped default changes.
- [ ] **Measure end-to-end wall time** -- the only NotMeasured headline metric. One full demo-asset run (translate + QA + scene) through the app, named dataset (voa10), on the demo machine. Record date, commit, config per the capture template in docs/metrics-definitions.md.
- [ ] **Freeze metrics snapshot** against final merged main: refresh test counts (dotnet test --list-tests + full run; npm test), re-add every token total from the named report files, update the main SHA.

## P1 -- demo

- [ ] **Record the 3:00 demo video** to demo-storyboard.md. Record voiceover first (~380 words), cut visuals to it. All UI shots from captured manifests/reports -- no live calls during recording. Disclosure card (Beat 1) and end card verbatim. No "Llama" wording.
- [ ] **Regenerate demo media locally**: demo-dub.mp4 (mux cleaned nosubs clip + dub-track.wav) and demo-dub-signshot.mp4 (via sign-shot). Media is gitignored; regenerate per the production notes.
- [ ] **Capture at 1920x1080**, verdict text legible at video scale; highlight the numbers that matter (2.46 / 2.32 / 1.39 / diff 0) in edit.

## P2 -- submission mechanics

- [ ] Confirm repo is public and clean; wwwroot prebuilt bundle current (cd web && npm run build if frontend changed).
- [ ] Verify a clean clone runs with one command (dotnet run --project src/Trackdub.Director.Server, open :5103) and GET /api/status reports hasKey:false without a key.
- [ ] Submit via Cerebral Valley before Nov 4 9:00 PM ET: repo link, video link, all contributors listed, named representative.

## Done (reference)

- Translation + timing gate, meaning gate, scene context (SAM 3.1), director adaptation, audition (measured TTS), visual localisation (Muse Image), static-shot gate, verify-only receipt lane. 259 .NET + 11 web tests green. Prebuilt web bundle committed. Cold start median 3.04 s.

## Notes

- Register-flip line choice (demo-asset-spec §8) is flagged not decided: either flip D5 and accept a subtle delta, or add one register-rich line from the 8-line sample as a separate exhibit. Michael's call.
- Meaning gate honest framing for the demo: 0 catches in 12 on real windows; 3 catches in 23 checks (2 of 20 lines) at 70% windows; 24/24 seeded errors, 0/16 false alarms on a set written for the eval. Never present the seeded score as accuracy on real dubs; never omit the fluency gap.
