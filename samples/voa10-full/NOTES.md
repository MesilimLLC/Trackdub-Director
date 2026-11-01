# VOA10 full-lesson dataset notes (Nov 1 expansion, Part 2)

Branch: `feat/transcribe-pipeline` (Part 1 transcribe stage present; ingest path used).
Scope: data work only. Transcribe + align + translate/QA. No scene, audition, or
localisation on this set. No commit.

## 1. Sources

- Video: full Lesson 10, `3ec2bbef-f064-4545-bbdd-74f2a695e141_fullhd.mp4`
  (80,544,980 bytes, 300.096 s, the "80 MB full-lesson file"; VOA page lists it as
  "1080p | 76.8MB"). Local copy from the media library (Downloads). Provenance:
  `http://learningenglish.voanews.com/a/lets-learn-english-lesson-10/3285228.html`.
- Audio: extracted locally with ffmpeg:
  `ffmpeg -i <mp4> -ac 1 -ar 16000 -c:a pcm_s16le lesson10-full.wav` → pcm_s16le,
  16 kHz mono, 300.096 s, 9,603,150 bytes (ffprobe).
- Script: published Lesson 10 "Come Over to My Place" conversation, same URL,
  fetched 2026-11-01; 18 lines (Anna/Ashley). Same script source as voa10 and
  voa10-scale.
- Transcription: the whole lesson in **1 accepted call** through the Part 1 ingest
  path (`trackdub-director ingest`, POST /v1/asr/transcribe, DIARIZATION,
  muse-voice-transcribe-1.0). No chunking was needed. Raw response preserved:
  `lesson10-transcribe-raw.json` (sessionId
  `voyager/transcribe/4B7890233919482A94F6946A6ACDE107`, audioDurationMs 300160).
  A first attempt (sessionId `…6A9CB3E19F314EAA8C33AC79B5F57648`) returned HTTP 200
  but was rejected by the app's shape validation: the 300 s response carries a
  speakerless 0.16 s turn (turn 41), which the initial parser did not admit. The
  parser was hardened (speaker is now nullable-verbatim; raw body is capturable via
  `ingest --raw`) and the retry succeeded. Transcribe calls this part: **2 of 4**.

## 2. What the lesson contains

- 0.00–19.98 s: titles/intro, no speech returned.
- Pass 1 (19.98–198.30 s): the conversation with Listen/Speak pedagogy interludes
  (turns 5–8, 11–15: "Listen.", "Speak.", drill repetitions). These are unscripted
  teaching material — excluded from the aligned set.
- 198.30–207.34 s: transition; no speech returned.
- Pass 2 (207.34–297.26 s): a straight replay of the full conversation.
- 297.26–300.10 s: end card, no speech returned.
- Total source duration: **300.096 s (5.0016 min)**.

## 3. Alignment (lesson-absolute seconds; duration = turn endMs − startMs, 0.1 s)

Speaker labels are the diarization labels verbatim (A/B/C/D/E), never mapped to
character names. Script speaker for reference: Anna = lines 1,2,4,6,8,10,12,14,16,17;
Ashley = 3,5,7,9,11,13,15,18. The diarizer's labels do not follow the script
speakers consistently (e.g. Ashley's lines come back labeled C or B); labels are
recorded as returned.

### Pass 1 (ids voa10f-0xx)

| id | script line | turn stamps (s) | dur | notes |
|---|---|---|---|---|
| voa10f-001a | L1 (first half) | 19.98–28.46 | 8.5 | transcript splits script L1 across two turns; split kept (see §4) |
| voa10f-001b | L1 ("Oh! That's Ashley calling.") | 29.02–32.14 | 3.1 | second half of the L1 split |
| voa10f-002 | L2 | 32.54–33.02 | 0.5 | transcript "Hi Ashley." |
| voa10f-003 | L3 | 33.10–37.82 | 4.7 | exact match |
| voa10f-004 | L4 | 37.98–43.74 | 5.8 | exact match |
| voa10f-005 | L5 | 79.26–81.82 | 2.6 | follows the L4 Listen/Speak drill (turns 5–8, excluded) |
| voa10f-006 | L6 | 81.82–91.50 | 9.7 | exact match |
| voa10f-007 | L7 | 140.14–146.86 | 6.7 | follows the L6 drill (turns 11–15, excluded) |
| voa10f-008 | L8 | 147.18–150.30 | 3.1 | exact match |
| voa10f-009 | L9 | 150.62–154.14 | 3.5 | exact match |
| voa10f-010 | L10 | 155.82–156.70 | 0.9 | transcript "Hi Ashley" |
| voa10f-011a | L11 ("Anna,") | 156.86–158.22 | 1.4 | transcript splits L11; hears "Hello Anna" |
| voa10f-011b | L11 (rest) | 158.22–161.58 | 3.4 | duplicated-word garble "there there" |
| voa10f-012 | L12 | 161.98–166.30 | 4.3 | exact match |
| voa10f-013 | L13 | 166.46–170.22 | 3.8 | exact match |
| voa10f-014 | L14 | 170.54–172.86 | 2.3 | exact match |
| voa10f-015 | L15 | 173.26–176.46 | 3.2 | exact match |
| voa10f-016 | L16 | 181.02–189.82 | 8.8 | garbles "Santa Donna" for "It's Anna! It's Anna!"; canonical script text kept |
| voa10f-017 | L17 | 189.82–198.30 | 8.5 | transcript prepends unscripted "oh"; canonical kept |

### Pass 2 (ids voa10f-1xx; straight replay, no pedagogy)

| id | script line | turn stamps (s) | dur | notes |
|---|---|---|---|---|
| voa10f-101a | L1 (first half) | 207.34–215.26 | 7.9 | same L1 split as pass 1 |
| voa10f-101b | L1 ("Oh! That's Ashley calling.") | 215.58–218.94 | 3.4 | leading "oh" merged into this turn |
| voa10f-102 | L2 | 219.10–220.06 | 1.0 | — |
| voa10f-103 | L3 | 220.06–224.70 | 4.6 | exact match |
| voa10f-104 | L4 | 224.78–228.78 | 4.0 | exact match |
| voa10f-105 | L5 | 228.86–231.58 | 2.7 | exact match |
| voa10f-106 | L6 | 231.58–239.42 | 7.8 | duplicated-word garble "metro and metro" |
| voa10f-107 | L7 | 239.50–245.98 | 6.5 | duplicated-word garble "metro metro" |
| voa10f-108 | L8 | 246.30–249.58 | 3.3 | exact match |
| voa10f-109 | L9 | 249.66–253.02 | 3.4 | exact match |
| voa10f-110 | L10 | 254.70–255.66 | 1.0 | — |
| voa10f-111a | L11 ("Anna,") | 255.82–256.70 | 0.9 | hears "Hello Anna"; same L11 split |
| voa10f-111b | L11 (rest) | 257.02–260.54 | 3.5 | exact match |
| voa10f-112 | L12 | 260.78–265.34 | 4.6 | follows speakerless 0.16 s turn 41 (260.62–260.78, excluded) |
| voa10f-113 | L13 | 265.66–269.18 | 3.5 | echo garble "I see see it" |
| voa10f-114 | L14 | 269.26–271.82 | 2.6 | exact match |
| voa10f-115 | L15 | 271.98–275.50 | 3.5 | exact match |
| voa10f-116 | L16 | 280.38–290.94 | 10.6 | garbles "Donna! Donna!" for "It's Anna! It's Anna!", trailing "Oh."; canonical kept |
| voa10f-117 | L17 | 291.58–297.26 | 5.7 | exact match |

## 4. Merges/splits against the script, stated plainly

- **Splits:** script L1 splits into two turns in both passes (greeting + "Oh! That's
  Ashley calling."); script L11 splits into two turns in both passes ("Anna," +
  the question). Both splits are kept as two segments each (the turn boundary is a
  natural dubbing unit); each pair is one script line, documented here.
- **Merges:** no two script lines were merged into one turn. L16 (both passes)
  carries garbles and, in pass 2, a trailing unscripted "Oh."; L17 pass 1 carries a
  leading unscripted "oh". Canonical script text is kept on those segments, as in
  the voa10/voa10-scale sets.
- **Missing:** script L18 (Ashley: "Great!") was **not returned** by the
  full-lesson transcription in either pass. The prep's meeting-scene clip
  transcription (2026-10-30) did capture it at source ≈ 201.5–202.7 s (clip-relative
  21.50–22.70 + 180.0 s offset), inside the 198.30–207.34 s region where this run
  returned no turn. Recorded as an ASR omission on the full-lesson input, not
  edited in from the clip run.
- **Excluded as unscripted:** the Listen/Speak pedagogy turns (5–8, 11–15) and the
  empty speakerless turn 41.

## 5. QA run (en→es, max_attempts 3, tolerance 0.10, cps 15, reasoning_effort low)

Reports: `qa-report-pass1.json`, `qa-report-pass2.json` (two `trackdub-director qa`
runs, 19 segments each, so the 120-call cap could be honored between runs; the cap
was not hit — 80 calls total, and the two reports re-add to the figures below).

```
date: 2026-11-01
commit: feat/transcribe-pipeline working tree (uncommitted)
dataset: voa10-full (38 segments; 18 script lines x 2 passes, L18 not returned by ASR)
config: max_attempts=3, tolerance=0.10, cps=15, reasoning_effort=low
reports: qa-report-pass1.json, qa-report-pass2.json
first_pass: 36/38 (94.7%)   final_pass: 38/38 (100%)   revisions: 3 (0.079/segment)
meaning: catches=1, not_evaluated=2, errors=0
  catch: voa10f-002 rev#2 "¡Hola!" — timing pass, meaning fail ("Named entity
  'Ashley' omitted"); rev#3 "¡Ashley!" passed both gates.
  not_evaluated: voa10f-002 first attempt and voa10f-014 first attempt (timing
  failed; meaning deliberately skipped, zero meaning tokens).
tokens: translation=11388 (41 calls), meaning=22650 (39 calls), qa total=34038
  (prompt 16499 + completion 17539; re-added from per-attempt fields, matches
  18265 + 15773 report totals)
tokens per finished minute: 34038 / 5.0016 = 6805.4 tokens/min
wall_time_e2e: not measured (two batched runs, not one timed run)
notes: model nondeterminism applies; numbers are this run, this config. Pass 2
  needed zero revisions; the only meaning catch of the run came from a pass-1
  revision that over-shortened a greeting.
```

Data in this folder is intentionally left uncommitted.
