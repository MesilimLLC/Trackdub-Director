# Emotion tag probe — record (2026-11-02)

Question: can the scene stage's emotion_tone_label be carried into ElevenLabs
generation, and on which model?

## Method

4 voa10 lines in their own speaker's voice (intended emotion from the line,
disclosed: cheerful greeting, warm farewell, curious question, surprised
realization) x 4 conditions: v2 plain, v2 tagged, v3 plain, v3 tagged.
16 syntheses, 636 characters billed. Durations measured from the returned
audio (measurements.json); clips in audio/, blind copies in blind/ with the
pack in blind-pack.md and the key in KEY.md.

## Findings

- Durations: v2 tagged ran +0.51 to +1.72 s longer on every line; v3 tagged
  stayed within plus or minus 0.16 s of v3 plain; v3 plain ran slower than
  v2 plain throughout.
- Listening verdict (Michael, 2026-11-02, definitive): on
  eleven_multilingual_v2 the dub literally speaks whatever tag is put in the
  text. On eleven_v3 the tags work as intended, and the tagged v3 clips read
  as the intended emotion.

## Decision

Tags are a working lever only on eleven_v3. Implementation (this repo):
EmotionTagOptions, disabled by default; when enabled, the mapped tag is
prepended at synthesis time only, gated on per-model support
(ITtsProvider.SupportsEmotionTags; ElevenLabs returns true only for
eleven_v3*), and never becomes part of the take text the meaning gate
judged. The emotion label reaches the audition from the scene stage's
DirectorContext. Switching the production model to v3 remains a separate
decision: v3 re-times every take, so windows and winning takes must be
re-measured under it before any default changes.
