# Recovery note: voa10-005 take 1

`audition-manifest.json` records voa10-005 take 1 (`voa10-005-take1.wav`, 2.4613125 s against a 2.2 s window, timing Fail) but the
audition writer of that time only saved winning takes, so the file was never saved locally. This note records how it was recovered.

- Recovered on: 2026-10-31
- Source: ElevenLabs account generation history, `GET /v1/history` (1 page, 61 items, `has_more=false`), then `GET /v1/history/{id}/audio`.
  No generation call of any kind was made; no replacement was synthesized.
- History item ID: `lwK1rZdHW8oei0AgPwrT`
- Matched on: text `¡Ok! ¡Adiós, Ashley! ¡Nos vemos pronto!` (exact), voice `Xb7hH8MSUJpSbSDYk0k2` (Alice - Clear, Engaging Educator),
  model `eleven_multilingual_v2`, generated 2026-10-31 13:19:44 UTC, which is the capture window of this manifest (the take WAVs are stamped 06:19 local).
- Returned audio: `audio/pcm`, 78,762 bytes (16 kHz mono 16-bit PCM, the format the audition lane requests).
- Measured length (repo `AudioDuration.Seconds`): 2.4613125 s. The manifest records 2.4613125 s: an exact match.
- Saved as: `samples/voa10/media/audition/voa10-005-take1.wav` (gitignored), wrapped as WAV with the repo `AudioDuration.ToWav`.
  SHA-256 of the saved file: `022ca987d7340d5c3501029adff64c99aae42f6726e36131489f19e30422b60d`.

Caveat: the history item reports a character-count change of 17, not the 39 characters the manifest bills for this take. Text, voice, model, timestamp
and exact duration all match, so the identification stands, but the billing figure was not reconciled.

The manifest itself is unchanged: it is evidence. This file is a loser take, so `verify-only` does not depend on it.
