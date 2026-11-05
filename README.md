# Trackdub Director

**A dubbing workbench that can say no, and shows its work.** AI dubbing fails
quietly: a translated line runs long, drops the subject, or a sign in the shot
stays in English, and nothing downstream checks. Trackdub Director puts
verification gates between every stage and records a verdict, with provenance
and token cost, for every line and frame. Built on the Meta Model API for the
Meta AI Developer Hackathon's **Build for Your World** track (Oct 26 - Nov 4, 2026).

## What was built during the event

**All code, data files, and measurements in this repository were created
during the hackathon (Oct 26 - Nov 4, 2026)**, apart from the pre-existing
material named below. This is a standalone app. It does not import, copy, or call
the author's earlier project Trackdub
([github.com/MesilimLLC/Trackdub](https://github.com/MesilimLLC/Trackdub)),
which predates the event and is not part of this submission. The problem
framing comes from that experience; the code does not.

Pre-existing material: VOA Learning English footage (public domain) and
ElevenLabs stock voices.

| Lane | What it does | Meta model |
|---|---|---|
| Translate + timing gate | Translates each line, estimates spoken length against the original window, bounces failures back to the model to revise (up to 3 attempts) | `muse-spark-1.1` |
| Meaning gate | Judges whether the translation still says what the source said; skipped (`NotEvaluated`, zero tokens) when timing already failed | `muse-spark-1.1` |
| Scene context | Grounds concept phrases (for example the shop sign) in each line's frame; a vision pass reads on-screen text and tone | `sam-3.1`, vision model (`META_VISION_MODEL`) |
| Director adaptation | Uses scene context to request a register change; the adapted line must pass both gates again | `muse-spark-1.1` |
| Audition | Synthesizes takes and measures the real audio against the window; never forces a winner | ElevenLabs TTS (the Meta catalog has no TTS endpoint) |
| Visual localisation | Repaints a crop of the sign and composites only the box back; pixels outside the box are machine-checked against the source | `muse-image-1.0` |
| Static-shot gate | Extends one sign repaint across a whole shot, only if every frame's sign region still matches the repainted frame | none (reuses the one repaint) |

`muse-voice-transcribe-1.0` powers the browser workbench's WAV transcription step. It was also used during dataset preparation (3 calls,
recorded in `docs/metrics-snapshot.md`).

## Measured results (VOA Lesson 10 coffee-shop segment)

Every figure comes from a report file in this repo; method in
`docs/metrics-definitions.md`, snapshot in `docs/metrics-snapshot.md`.

- **Timing loop:** 83% of lines fit their window on the first translation,
  100% after bounce-back revisions (voa10, 6 lines, 2 revisions).
- **Measured audio:** the audition lane rejected a take that measured 2.46 s
  against a 2.2 s window and cast the 2.32 s take; in another run it refused
  to cast a line whose only take ran 1.39 s against a 1.32 s ceiling.
- **Meaning gate, real timing:** 0 catches in 12 evaluated candidates on
  these clips. It ran and cost tokens on every one; it found nothing to stop.
- **Meaning gate under pressure:** with every window tightened to 70% (a
  stand-in for a faster original performance), the timing loop had to
  compress hard, and the meaning gate stopped 3 of 23 timing-passing
  candidates on 2 of 20 lines. A timing-only loop would have shipped "¿Rodamos
  mañana?" ("are we shooting tomorrow?") for "Where are we shooting
  tomorrow?". Known gap: it judges meaning, not grammar, and passed
  ungrammatical shorthand such as "Gracias x hoy"
  (`samples/meaning-eval/qa-report-pressure-70.json`).
- **Meaning gate on labeled cases:** caught 24 of 24 seeded errors
  (negation, numbers, names, role swaps, dropped or added content, modality,
  time) with 0 false alarms on 16 clean translations, including idiomatic and
  formal-register ones. The 40-case set was written for this eval, so treat
  it as a check that the gate sees clear material errors, not as accuracy on
  real dubs (`samples/meaning-eval/meaning-eval-report.json`).
- **Sign localisation:** 3 of 3 frames repainted COFFEE SHOP to CAFETERÍA
  with an outside-box pixel difference of 0.
- **Video-rate sign:** the static-shot gate checked all 260 frames of the
  10.4 s sign shot: box mean SSIM 0.9864 or higher (threshold 0.95) and no
  8x8 block brighter or darker by more than 13.6 luma levels (limit 30). It
  composited one repaint across the shot, outside-box difference 0 on every
  frame (`samples/voa10/sign-shot-report.json`). Starting the window one
  frame early takes in the previous shot (box SSIM 0.28), and the gate
  refuses and writes no video (`samples/voa10/sign-shot-refused-report.json`).
- **Open-weight lanes (opt-in, measured, never the default):** Muse Spark
  Open served as an independent meaning judge (73 of 75 agreement with the
  hosted judge), produced a gate-verified translation track for the 6 demo
  lines, and translated the 40-line eval set with meaning 40 of 40 and
  timing 22 of 40; Muse Glimmer ran as a recovery agent and recovered all
  4 refused attempts across 3 lines, with two winning lines telegraphic
  (`samples/spark-open/`, `samples/glimmer-recovery/`).
- **Pacing probe (Nov 4):** model-generated pacing labels were unreliable:
  six single-clip labels collapsed to `conversational`, and a four-clip
  ranking reversed the measured speech-rate order. Keep measured speech rate
  as the source of pacing judgments; see the [audio-pacing notes](samples/audio-pacing/NOTES.md).
- **Reviser dataset audit (Nov 4):** the captured reports produced no
  leakage-clean training pairs. All 14 failed-attempt/accepted-final pairs
  came from six source lines shared with frozen benchmarks, so the training
  and holdout files are correctly empty; see the [reviser notes](samples/finetune-reviser/NOTES.md).
- **Tests:** 486 .NET tests executed and passed (484 listed cases) and
  12 web tests at `9ababf0`.

## Adoption

The verification gates sit behind provider interfaces (translation, TTS,
image), so an existing dubbing pipeline can adopt the timing and meaning
verification layer without adopting the whole app: implement the interface
against your own providers and the QA loop, meaning gate, and revision
bounce-back work unchanged. Voice in the audition lane is ElevenLabs TTS
because the Meta model catalog offers no TTS endpoint; this is disclosed
wherever the audition is described.

## Architecture

```
Trackdub.Director.slnx
src/Trackdub.Director.Core/      Class library: MetaAuth (env-only auth), MetaClient
                                  (https://api.meta.ai/v1), TranslationService, QaService +
                                  DurationEstimator (timing gate), MeaningGate,
                                  SceneContextService (sam-3.1 + vision pass),
                                  DirectorAdaptationService, AuditionService (ElevenLabs TTS,
                                  measured audio), VisualLocalisationService (muse-image-1.0),
                                  StaticShotCompositor (static-shot gate),
                                  FfmpegDubExportService (muted-original MP4 export).
                                  No third-party packages; HttpClient + System.Text.Json only.
                                  API keys are never read from files and never logged.
src/Trackdub.Director.Server/    ASP.NET Core minimal API referencing Core (see Server API).
                                  Serves the prebuilt web UI from wwwroot;
                                  no key required to start; key only gates live endpoints.
src/Trackdub.Director.Cli/       Console app (trackdub-director):
                                  smoke / translate / models / qa / sign-shot / meaning-eval / verify-only.
web/                             React + Vite + TypeScript frontend. A Windows-hosted
                                  browser stage workbench: editable segments and media,
                                  shared run settings, fixed stage workflows, evidence
                                  downloads, audition audio playback, basic dubbed-MP4
                                  export, a visual box editor, and an advisory Copilot that can only
                                  propose settings for the user to apply.
                                  Dev proxy: Vite forwards /api to :5103.
tests/Trackdub.Director.Tests/   xUnit. Live tests skipped without key; fake-handler
                                  estimator/QA tests; server endpoint tests (WebApplicationFactory).
samples/segments.json            8-line sample with duration_seconds.
```

The server reuses Core shapes directly — `TranslationBatchOutput`, `QaReport`,
etc. — so the JSON the UI receives from `POST /api/qa` is the same shape the
CLI writes to `qa-report.json`.

## Setup

Requires .NET 10 SDK (10.0.300+; pinned via `global.json`) and Node 18+ for the frontend.

Set the Meta Model API key in your environment. Never commit a key to this
repo; `.env` is gitignored and `.env.example` holds placeholders only.

PowerShell:

```powershell
$env:META_API_KEY = "<your key>"
```

bash:

```bash
export META_API_KEY="<your key>"
```

Optional overrides: `META_MODEL` (default `muse-spark-1.1`),
`META_MODEL_API_BASE_URL` (default `https://api.meta.ai/v1`),
`META_REASONING_EFFORT` (default `low`; accepted `low|medium|high`).
`META_MODEL_API_KEY` is accepted as a fallback for `META_API_KEY`.

## Running the app

Run the backend on Windows for the full workbench. Frame decoding uses
Windows Imaging Component (WIC); on another host OS the image-dependent
stages fail closed instead of using a fallback codec. The browser is only
the client and does not remove that backend requirement.

### Single-command mode (prod-like)

A clean clone runs the whole app with one command:

```bash
dotnet run --project src/Trackdub.Director.Server
# open http://localhost:5103
```

The prebuilt web UI is committed under wwwroot so the app runs with a single
dotnet run; rebuild it with `cd web && npm run build` if you modify the web
frontend.

No key is required to start — the UI loads and `GET /api/status` reports
`hasKey: false` until a key is configured. Live endpoints
(`POST /api/translate`, `POST /api/qa`, `GET /api/models`) return 401/502 with
a safe error when no Meta key is set. `ELEVENLABS_API_KEY` is needed only for
live audition runs (`POST /api/audition`); when unset the audition lane fails
closed with zero calls.

### Development mode (Vite + backend)

Two terminals:

```bash
# Terminal 1 — backend on :5103
dotnet run --project src/Trackdub.Director.Server

# Terminal 2 — frontend on :5173 with /api proxy to :5103
cd web && npm install && npm run dev
# open http://localhost:5173
```

Vite config (`web/vite.config.ts`) proxies `http://localhost:5173/api/*` to
`http://localhost:5103`, so the dev UI talks to the same API.

To refresh the prod bundle after frontend changes: `cd web && npm run build`.

## Browser stage workbench

The browser surface is a stage workbench with a basic dubbed-MP4 export:
the original picture with its audio omitted and the winning ElevenLabs takes
placed at explicit start times. It is not a timeline editor or lip-sync
system. It supports two ordinary-input journeys without hand-written JSON:

1. **Dub:** type segments or transcribe a WAV, set languages and the shared
   QA options, then run Translate, QA, Scene, Director, Audition, or the
   fixed Pipeline template (Transcribe when audio is supplied, then QA,
   Scene, and optional Audition). Valid WAVs are converted in the browser to
   mono 16-bit PCM at 16 kHz when needed and checked again before the ASR
   provider call. Transcribed speaker labels A/B default to Anna/Ashley; review
   or change those mappings before audition. Winning and losing takes stream
   from temporary server files with byte-range playback; files expire after an
   hour, are capped at 512 MiB total, and are removed when the server stops.
   Downloadable evidence JSON omits audio URLs.
   Set each segment's **Start** time (ASR turns supply `start_seconds`),
   choose the original MP4/MOV/M4V/WebM/MKV, and choose **Export dubbed
   MP4**. Export refuses unless every segment has a start and a winning
   take whose measured audio fits without overlapping another winner.
2. **Visual:** upload a PNG, JPEG, or WebP frame (10 MiB, 4096 px, and
   16,777,216-pixel limits), drag a sign box in original-image coordinates,
   repaint it, inspect `outside_box_diff`, and download the localised PNG.

Input edits invalidate only the evidence that depends on them. For example,
changing only a Director register keeps the prerequisite QA evidence, while
changing dialogue clears text-dependent reports and any pending Copilot
proposal. The QA loop's `maxAttempts` setting is labelled **Maximum
revisions**: it is the number of revisions allowed after the first attempt.

Pipeline and standalone Audition reuse a browser-stored `Idempotency-Key` for
20 minutes. In the running server process, retrying a dropped browser response
returns the same result. The server also keeps a small local ledger of request
keys, payload fingerprints, and timestamps (no request text or audio). After a
server restart, reusing a recorded key reports an unknown provider outcome and
does not execute the request again; if recovery storage is unavailable, paid
requests fail closed. Set `TRACKDUB_IDEMPOTENCY_PATH` to choose the ledger path.
Starting a new run can repeat paid stages, so check provider history first when
the previous outcome is unknown.
Meta chat retries HTTP 429 and 5xx responses that do not report billed usage,
using bounded exponential backoff with jitter. Transcription retries 429 and
one explicit HTTP 500. A 5xx response with usage data is surfaced without a
retry. Timeouts and interrupted response bodies remain outcome-unknown and are
not automatically resent. The optional Copilot makes one disclosed Meta call
and returns a bounded, revision-bound settings proposal. **Apply settings** is
the only action it can lead to; it never runs a stage, and there is no
"Approve & run" path.
Visual localisation edits one manually boxed still; moving, rotating,
perspective-changing, and occluded signs are not tracked. Video-rate
`sign-shot` packaging remains outside the browser workbench. Verify-only
remains a CLI/server check over captured run folders already on
the server disk, not a verifier for the current browser session. Dub export
requires `ffmpeg` and `ffprobe` on the backend PATH (or `FFMPEG_PATH` and
`FFPROBE_PATH`); the source video is capped at 200 MiB, and the output is
H.264/AAC MP4 with no original audio track.

## Smoke test

One real Meta Model API call translating a single line from English to
Spanish:

```bash
dotnet run --project src/Trackdub.Director.Cli -- smoke
dotnet run --project src/Trackdub.Director.Cli -- smoke --text "Your line here."
```

Expected shape:

```text
EN: The director checks every line before it reaches the audience.
ES: <Spanish translation returned by the model>
```

## Translation lane

Batch translate a JSON file of `{id, text}` segments:

```bash
dotnet run --project src/Trackdub.Director.Cli -- translate --input samples/segments.json --output translated.json
dotnet run --project src/Trackdub.Director.Cli -- translate --input samples/segments.json --output translated.json --from en --to es
```

Input (`samples/segments.json`):

```json
[
  { "id": "seg-001", "text": "The director checks every line before it reaches the audience.", "duration_seconds": 4.6 }
]
```

`duration_seconds` is optional per segment. It is used by the QA loop (see
below); the plain `translate` command ignores it. When absent, the QA loop
estimates the window from the source line's own spoken duration at the
configured CPS.

Output: each segment gains `translation`, `provenance` (model ID, endpoint host,
UTC timestamp, prompt template version), and `usage` (prompt/completion/total
tokens per line and totaled). See `samples/segments.json` for a runnable 8-line
sample.

List models available to this account:

```bash
dotnet run --project src/Trackdub.Director.Cli -- models
```

## QA / timing loop

The `qa` subcommand runs translate plus a deterministic timing check over an
input file and writes a QA report JSON. Per segment: the timing window (explicit
`duration_seconds` or source-estimated), the first-attempt estimate and verdict,
each revision attempt, the final verdict, and token usage across all attempts.
A line that still fails after max attempts is reported as `Fail`, never
silently accepted. A translation or revision call that fails is recorded on its
own segment (a failed initial translation leaves `first_attempt` null; a failed
revision keeps the completed first attempt; either way the error goes in
`generation_error` and any tokens the API billed in `token_usage`) and the run
continues with the remaining segments, so a failed line never erases the
completed segments or their billed usage. Totals carry `failed_segments` for
every such generation failure, whether the first attempt completed or not.

Configurable: `--max-attempts` (default 3), `--tolerance` (fraction, default
`0.10` = 10%), `--cps` (characters per second for the target language, default
`15`). Env overrides `QA_MAX_ATTEMPTS`, `QA_TOLERANCE`, `QA_CPS_<LANG>`,
`META_REASONING_EFFORT`. The duration estimator is `seconds = chars / CPS` with
per-language CPS.

```bash
dotnet run --project src/Trackdub.Director.Cli -- qa --input samples/segments.json --output qa-report.json
dotnet run --project src/Trackdub.Director.Cli -- qa --input samples/segments.json --output qa-report.json --max-attempts 3 --tolerance 0.10 --cps 15
```

Report shape (`qa-report.json`):

```json
{
  "source_language": "en",
  "target_language": "es",
  "qa_options": { "max_attempts": 3, "tolerance_fraction": 0.1, "target_cps": 15, "source_cps": 15, "reasoning_effort": "low" },
  "segments": [
    {
      "id": "seg-001",
      "window_seconds": 4.6,
      "first_attempt": { "translation": "...", "estimated_seconds": 4.07, "verdict": "Pass", "prompt_tokens": 52, "completion_tokens": 96 },
      "revisions": [],
      "final_verdict": "Pass",
      "final_translation": "...",
      "token_usage": { "prompt_tokens": 52, "completion_tokens": 96, "total_tokens": 148 }
    }
  ],
  "totals": { "total_segments": 8, "first_pass_count": 4, "final_pass_count": 8, "total_revisions": 4, "first_pass_rate": 0.5, "final_pass_rate": 1.0, "total_tokens": 4398, "failed_segments": 0 }
}
```

How to read the pass-rate report: the console prints the same summary as the
`totals` block. `First-pass` is the share of segments that fit their window on
the initial translation. `Final` is the share after bounce-back revisions.
`Revisions` is the total number of revision calls. Tokens are summed across
all attempts. A non-zero gap between first-pass and final shows the loop
working; a final pass rate below 100% means at least one line was reported as
`Fail` after exhausting attempts.

## Server API

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/status` | none | `{ hasKey, model, baseUrl, reasoningEffort }` — never the key |
| GET | `/api/models` | key | Account models (proxies `GET /models`) |
| POST | `/api/translate` | key | `{ segments, sourceLanguage?, targetLanguage? }` → `TranslationBatchOutput` |
| POST | `/api/qa` | key | `{ segments, sourceLanguage?, targetLanguage?, options? }` → `QaReport` (same JSON as CLI report) |
| POST | `/api/scene-context` | key | Per-segment frames + concept phrases → `DirectorContext` per segment (sam-3.1 regions, vision fields) |
| POST | `/api/director-adaptation` | key | Register adaptation from scene context; adapted lines re-run both gates |
| POST | `/api/audition` | key + `ELEVENLABS_API_KEY` | Synthesizes takes, measures audio against each window, casts or refuses per line |
| POST | `/api/visual-localisation` | key | Frame + box + source/target text → localised frame with `outside_box_diff` |
| POST | `/api/transcribe` | key | WAV audio → editable transcript segments |
| POST | `/api/pipeline` | key (+ ElevenLabs for audition) | Fixed Transcribe → QA → Scene → optional Audition template |
| POST | `/api/copilot/propose` | key | One advisory Meta call returning a bounded settings proposal; never runs a stage |
| POST | `/api/verify-only` | none (local only) | Verifies a captured run already on the server disk; not the browser session |
| POST | `/api/export-dub` | none (local only) | Multipart original video + explicit segment starts + winner WAVs → muted-original dubbed MP4; refuses missing, overlapping, or overrunning winners |

`POST` bodies accept `segments` as `[{ id, text, duration_seconds?, start_seconds? }]` plus
optional `sourceLanguage`/`source_language`/`from` and
`targetLanguage`/`target_language`/`to` (default `en` → `es`). `POST /api/qa`
also accepts `options: { maxAttempts, toleranceFraction, targetCharactersPerSecond, reasoningEffort }`.

## Tests

```bash
dotnet test
cd web && npm test
```

Live .NET tests (smoke, translation batch, QA) run only when a Meta API key is
present; otherwise they return early (skipped, not failed) so the suite stays
green offline. Deterministic tests use a fake HTTP handler: estimator unit
tests, a line that passes first try, a line that fails then passes on revision,
and a line that never fits and is reported failed after max attempts. Server
endpoint tests use `WebApplicationFactory` with the same fake handler.

Frontend: one Vitest smoke test rendering the line list from a mocked API response.

## Layout

```text
Trackdub.Director.slnx
src/Trackdub.Director.Core/    Class library: Meta Model API client, translation, duration estimator, QA loop
src/Trackdub.Director.Server/  ASP.NET Core host exposing Core over HTTP + serving the built frontend from wwwroot
src/Trackdub.Director.Cli/     Console app: smoke / translate / models / qa / sign-shot / meaning-eval / verify-only subcommands
web/                           React + Vite + TypeScript frontend (one screen)
tests/Trackdub.Director.Tests/ xUnit tests + server endpoint tests
samples/segments.json           8-line sample input with duration_seconds for the qa command
samples/voa10/                 VOA Lesson 10 coffee-shop dataset: segments, frames, manifests, reports
docs/                          Metrics definitions and snapshot, demo asset spec, judging criteria
.env.example                   Placeholder config - no real keys
global.json                    Pins .NET 10 SDK
```

## Sign shot (video-rate sign localisation)

One Muse Image repaint can stand in for a whole shot only when the sign
region stays the same during it. `sign-shot` checks that before compositing:
every frame in the window must keep its box region at or above a mean luma
SSIM threshold (default 0.95) against the frame that was repainted, and no
8x8 block may shift its mean brightness by more than 30 levels, so a small
occlusion that the mean would average away still fails. An occluder close in
brightness to what it covers can pass; that is a limit of the metric. If any
frame fails, nothing is written. Needs `ffmpeg` and `ffprobe` on PATH (or
`--ffmpeg`/`--ffprobe`); frames move as raw RGB over pipes.

```bash
dotnet run --project src/Trackdub.Director.Cli -- sign-shot   --video clip.mp4 --reference samples/voa10/frames/hero-0242.jpg   --localised samples/voa10/frames/localised/hero-0242-cafeteria.png   --box 449,145,751,534 --start 2.36 --end 12.76   --output clip-localised.mp4 --report sign-shot-report.json
```

Exit code 0 means composited (or passed, when `--output` is omitted), 2
means refused. The report records per-frame SSIM and largest block shift, the first failing frame,
and the largest outside-box pixel difference, counted on decoded frames
before the lossy h264 encode.

## Meaning eval

`meaning-eval` runs the meaning gate (the same call the QA loop makes) over
labeled cases and reports catches, misses, false alarms, recall, and
false-positive rate. A case the gate could not judge (API error) counts in
neither rate.

```bash
dotnet run --project src/Trackdub.Director.Cli -- meaning-eval \
  --input samples/meaning-eval/cases.json --output meaning-eval-report.json
```

Cases are `[{ id, source_text, candidate, expected: "Pass"|"Fail", error_type?, note? }]`.
`samples/meaning-eval/NOTES.md` describes the shipped case set and the 70%
pressure dataset.

## Verify-only receipt

`verify-only` is a CI-style gate over a captured run, with zero network
calls. It is the one-click verification path: one command re-derives every
verdict from the recorded numbers and says Verified or HumanReviewRequired. It reads `audition-manifest.json` and recomputes every verdict from
the recorded numbers; no summary field (cast flag, winner flag, timing
verdict) is trusted as a pass. A line is **Verified** only if it has a
winning take, that take re-passes the QA lane duration check against the
stamped window, a meaning Pass is recorded for its text, and the winning
media file is on disk. Anything else is **HumanReviewRequired** with
reasons per line (`Uncast`, `TimingFail`, `MeaningFail`,
`MeaningNotEvaluated`, `MediaNotPresent`; `MediaHashMismatch` when a recorded hash, from the
manifest or the sidecar below, differs from the file). Not evaluated is a
review reason, never a pass.

```bash
dotnet run --project src/Trackdub.Director.Cli -- verify-only            # samples/voa10
dotnet run --project src/Trackdub.Director.Cli -- verify-only <run-folder> --media-dir <dir> --out receipt.json
```

Exit codes: 0 all Verified, 1 at least one HumanReviewRequired, 2 usage
error or unreadable input. The receipt is written beside the input as
`verify-receipt.json` plus a standalone `verify-receipt.html` (inline CSS,
no scripts, no external assets), rendered from the receipt JSON alone. The
server exposes the same gate as `POST /api/verify-only` with
`{ "run": "voa10" }`: it reads a run folder already on disk inside the
captured-runs root (`samples/`, or the `VerifyOnly:RunsRoot` setting) and
returns the receipt JSON. There is no upload path. The server has no client
authentication, so keep it on the local machine, and do not point
`VerifyOnly:RunsRoot` at nonpublic runs while it is reachable
from elsewhere. Run folders that are, or sit behind, a symbolic link are refused.

The receipt is hash-sealed: the manifest and every present winning media
file are listed with their SHA-256, labelled `recomputed`. Media is
gitignored, so on a clean clone the winning files are absent: each line is
then HumanReviewRequired with `MediaNotPresent`. The hash shown comes from
the manifest if it recorded one (`recorded-at-capture`); otherwise from the
optional `audition-media-hashes.json` sidecar beside the manifest
(`recorded-in-sidecar`, computed after capture, so not claimed as capture
time); otherwise it says `not-recorded-in-manifest`. A recorded hash is never
shown as recomputed. The committed voa10 run uses the sidecar.
Audition runs now save every synthesized take (winners and losers) and
record each take's SHA-256 in the manifest; the older captured manifests
predate that and are kept as evidence, unchanged.

## Honest history

All dates are 2026; every commit is in this repository's history.

- Oct 26: Core and Meta Model API smoke test, translation lane with
  provenance and usage, QA/timing loop with `reasoning_effort` control,
  local web app (server plus React frontend).
- Oct 28: Meaning gate after the timing check. Director Stage A: scene
  context via `sam-3.1`. Demo asset spec and metrics definitions.
- Oct 29: Stage B1 vision enrichment, Stage B2 register adaptation with gate
  re-verification, CI for .NET and web.
- Oct 30: VOA Lesson 10 dataset. Audition lane: voice casting with measured,
  gated multi-take selection.
- Oct 31: Visual localisation (crop-edit-composite with `muse-image-1.0`),
  audition alternates and speed variant, uniform-config scale reports,
  prebuilt web bundle, static-shot gate for video-rate sign localisation,
  meaning-gate measurement (70% pressure run and `meaning-eval` over 40
  labeled cases).
