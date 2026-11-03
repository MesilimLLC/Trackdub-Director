# Emotion tag probe, v4 — record (2026-11-02)

Follow-on to the v3 probe (samples/emotion-probe): the expanded tag
vocabulary and eleven_v4, probed on the same terms.

## Method

4 voa10 lines in their own speaker's voice x 3 conditions on eleven_v4:
plain, emotion-tagged, emotion+pacing-tagged ([pauses], [slowly],
[briskly] by line intent). 12 syntheses, 537 characters billed. Durations
measured from the returned audio (measurements.json); blind copies in
blind/ with blind-pack.md and KEY.md.

## Findings (durations; delivery judged by listening)

| Line | v4 plain | v4 tagged | v4 tagged+pacing |
|---|---|---|---|
| voa10-001 (cheerful) | 1.20 s | 1.52 s | 1.44 s |
| voa10-005 (warm) | 2.40 s | 2.48 s | 2.96 s |
| voa10-002 (curious) | 3.36 s | 3.44 s | 3.04 s |
| voa10-004 (surprised) | 3.60 s | 3.68 s | 3.84 s |

No v2-style duration blowup: tagged rows stay within 0.32 s of plain
(v2 spoke the tags and added 0.5-1.7 s). Whether the delivery lands as
intended, and whether pacing tags read naturally, is for the blind pack
ratings; no adopt decision is recorded here without them.

## Code state

- EmotionTagMap expanded: roughly 50 emotion keywords plus pacing/pause
  keywords; emotion and pacing combine (one each), unmappable labels still
  get no tag, never an invented one.
- ElevenLabs tag gate now admits eleven_v3* and eleven_v4*; v2 and all
  other models still can never receive a tag.
- Default remains disabled, production model unchanged. Passing detected
  delivery straight through is exactly what the gated path does at
  synthesis time; it does not make v4 the default.
