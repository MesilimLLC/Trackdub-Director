# Hosted Spark audio-input probes for pacing (2026-11-04)

Question: can hosted Muse Spark take speech audio in a chat call and judge
delivery pacing, the way the Baseten Spark Open route was probed earlier?

## Route that works

`POST https://api.meta.ai/v1/chat/completions`, `reasoning_effort: low`, with
an OpenAI-canonical audio part:

```json
{"type": "input_audio", "input_audio": {"data": "<base64 WAV>", "format": "wav"}}
```

The Baseten-style `audio_url` part is rejected by the Meta API
(HTTP 400, "did not match any supported type"). No hosting needed: base64 in
the request body works, same as frame `data:` URLs in the vision lane.

## Per-model result, same clip (voa10-002 winner, known text)

Ground truth: "Anna, ¿cuál cafetería? Hay tres cafeterías." (tagged [briskly]
at synthesis; 3.12 s in a 4.4 s window).

| Model | Heard | Transcript | Emotion | Pacing | Tokens | Wall |
|---|---|---|---|---|---|---|
| muse-spark-1.1 | yes | exact, incl. accents and name | curious | conversational | 455 | 1.97 s |
| muse-spark-1.2 | yes | language drift ("Há três cafeterias") | neutral | conversational | 769 | 3.51 s |
| muse-spark-1.3 | yes | near-exact ("Ana", no final period) | curious | conversational | 762 | 3.19 s |

All three accept the audio part and return the requested strict JSON.
1.1 was the most accurate and the cheapest (170 reasoning tokens vs ~480).

## Pacing discrimination (muse-spark-1.1, forced-choice prompt)

Six `eleven_v4` clips from `samples/emotion-probe-v4` with known intended
pacing tags ([briskly], [slowly], [pauses], plain). Asked one at a time,
**every clip was labeled `conversational`** — zero discrimination in the
absolute label, although pause descriptions were mostly plausible.

Pairwise comparison (slow-tagged 005 as A vs brisk-tagged 002 as B, one call):
correct — A "unhurried", B faster. But a four-clip ranking call (ground truth
by measured chars/sec: 002 brisk 14.1 > 004 pauses 12.0 > 005 slowly 11.5 >
001 pauses 9.0) returned the ranking **exactly reversed**, calling the
slowest clip "brisk" and the fastest "slow".

## Verdict

- Hosted Spark audio input is real and usable: transcription and emotion
  labels are credible; 1.1 is the pick.
- Absolute pacing labels from the model are **not reliable**: single-clip
  labels collapse to "conversational", and multi-clip ranking inverted.
  The model appears to judge clip duration/energy rather than speech rate.
- Pacing speed should come from measurement the harness already owns
  (transcript length over turn duration from diarization, plus inter-turn
  gaps). The audio model's defensible contribution is emotion tone and pause
  placement, not the speed label.

Spend: 12 Meta calls (1 rejected shape, 11 successful), 7,424 tokens total.
Nothing integrated into production lanes; probe scripts were run from /tmp.
