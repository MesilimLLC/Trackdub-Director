# Emotion tag models: survey and open questions

Status: research notes, 2026-11-21. Nothing here is integrated or shipped.

## Goal

Assign a delivery label (emotion, and optionally pauses or pace cues) to each
dubbed segment so the TTS can speak it. The current path uses a hosted model
(Muse Spark). This document records which local alternatives exist, what each
one's license allows, and what still needs to be tested.

## Where we are

- **Muse Spark 1.1 (hosted)** gave the most accurate transcript and credible
  emotion labels in the probes, and was the cheapest of the three Spark
  versions tested (about 170 reasoning tokens, against about 480 for the
  others). See `samples/audio-pacing/NOTES.md`.
- **Pace labels from that model are unreliable.** Single clips all came back
  "conversational", and a four-clip ranking came back reversed. Pace should be
  measured (transcript length over turn duration, plus inter-turn gaps), not
  labelled by the model.
- **Label pass-through beat the keyword map.** Sending the detected label in its
  own words as one bracketed ElevenLabs tag sounded better than the old keyword
  map in a listening A/B on `eleven_v4`. The keyword map is retired. See
  `samples/emotion-passthrough/NOTES.md`.
- **Blind human ratings are the ground truth.** The v4 probe has blind packs in
  `samples/emotion-probe-v4/`. No adopt decision has been recorded from them yet.

## Candidates

| Model | Runs locally | License (from model card or repo) | Labels | Notes |
|---|---|---|---|---|
| Muse Spark 1.1 | No (hosted API) | Hosted service terms | Free-form | Current pick for labels. Pacing unreliable. |
| Qwen2.5-Omni-7B | Yes, 7B, GPU | Apache-2.0 (model card) | Free-form; label set defined in the prompt | Audio in. Clean license. Heavy on VRAM next to TTS on an L4. Not evaluated. |
| Qwen2-Audio-7B-Instruct | Yes, 7B, GPU | Apache-2.0 (model card) | Free-form | Older than Omni. Not evaluated. |
| emotion2vec+ large | Yes, about 300M params | FunASR Model License v1.1 | angry, disgusted, fearful, happy, neutral, other, sad, surprised, unknown | Utterance-level scores. Official PyTorch release. Third-party ONNX export: `Voldorn/emotion2vec-plus-large-onnx` (same license, max tested input 30 s). Not evaluated. |
| SenseVoice-Small | Yes, 241 MB int8 ONNX (`OpenVoiceOS/sensevoice-small-onnx`) | FunASR Model License v1.1 | HAPPY, SAD, ANGRY, NEUTRAL and others per community sources; not verified for this ONNX export | Emotion and transcript in one pass. Direct inference limited to 30 s. Languages: zh, yue, en, ja, ko. Not evaluated. |
| NVIDIA Audio2Emotion-v2.2 | Yes | NVIDIA Audio2Emotion license | Six emotions | **Excluded.** The license prohibits use of the model or its components for emotion recognition outside the Audio2Face project. |

## License findings

### FunASR Model License v1.1

Source: https://github.com/modelscope/FunASR/blob/main/MODEL_LICENSE

- Permits use, copy, modification, and sharing, including fine-tuned models.
- Requires attribution of the source and author, and retention of model names.
- Says the software is "provided for reference and learning purposes only". This
  conflicts with the free-use grant and does not say commercial use is allowed.
- Governing law is an unfilled placeholder: "[Country/Region]".
- Conduct clause: "unjustified denigration, malicious smearing, or baseless
  insults" against the software forfeits all licenses.
- Alibaba may revise the agreement; continued use counts as acceptance.
- No explicit commercial grant and no explicit commercial prohibition.

### Effect on our policy

The Trackdub repo's `docs/legal/MODEL_LICENSE_POLICY.md` governs this:

- The model cannot go in the bundled manifest on this license. Commercial use is
  not clearly granted.
- The accepted-risk exception requires a license that explicitly grants
  commercial use. The FunASR license does not, so the exception does not apply.
- emotion2vec+ large was fine-tuned on about 42,500 hours of pseudo-labelled
  data. Its training-data provenance is unverified.
- The FunASR-family models are usable for internal evaluation only, pending
  counsel review.

### Qwen models

Both Qwen2.5-Omni-7B and Qwen2-Audio-7B-Instruct list Apache-2.0 on their
Hugging Face cards. Before any manifest entry, confirm the license file in the
repository and pin the artifact with a SHA-256.

## Qwen3-TTS instruction control (related)

- Official README: the 1.7B CustomVoice model accepts an optional `instruct`
  string, free-form natural language. Examples: "用特别愤怒的语气说" (say it in
  a very angry tone) and "Very happy." Instruction control is listed for 1.7B
  only, not 0.6B.
- There is no fixed emotion or pacing tag vocabulary, and no pacing parameter.
  The README describes speaking rate as a soft cue that the model adapts to from
  instructions.
- In the Trackdub repo, `QwenTtsOptions.InstructText` exists, but
  `Qwen3TtsEngine` passes `instruct: null`, so the option has no effect today.
  The tokenizer has the instruct path, but it has not been tested against the
  ONNX export Trackdub uses.

## Open questions

1. Does counsel accept the FunASR license for commercial use, including the
   conduct clause and the unfilled governing law?
2. Do any local models beat Muse Spark on the blind ratings for the v4 probe
   clips?
3. Can Qwen2.5-Omni-7B fit alongside Qwen3-TTS on an L4, and does it reliably
   output our label vocabulary?
4. Does the 1.7B ONNX model honor `instruct`? Test one segment with and without.
5. Wire `QwenTtsOptions.InstructText`, or a per-segment label, through
   `Qwen3TtsEngine` in the Trackdub repo. Today the option is ignored.
6. Pace still has to come from measurement, whatever the labelling model.

## Suggested order

1. Qwen2.5-Omni-7B probe on the four `samples/emotion-probe-v4` lines in three
   conditions, free-form labels mapped to our vocabulary and scored against the
   blind ratings. Its license is the one that can ship.
2. Ask counsel about the FunASR license (open question 1) in parallel.
3. Internal comparison probe, research only: emotion2vec+ (ONNX) and
   SenseVoice-Small on the same clips. Their results only inform a product
   decision if counsel clears the license; otherwise they are a reference point
   for the Qwen labels.
4. Choose by blind ratings among the models that can ship.

## Sources

- Muse Spark audio probes: `samples/audio-pacing/NOTES.md` in this repo
- Label pass-through A/B: `samples/emotion-passthrough/NOTES.md` in this repo
- v4 emotion probe: `samples/emotion-probe-v4/NOTES.md` in this repo
- FunASR Model License v1.1: https://github.com/modelscope/FunASR/blob/main/MODEL_LICENSE
- emotion2vec+ large: https://huggingface.co/emotion2vec/emotion2vec_plus_large
- emotion2vec+ large ONNX: https://huggingface.co/Voldorn/emotion2vec-plus-large-onnx
- SenseVoice-Small: https://huggingface.co/FunAudioLLM/SenseVoiceSmall
- SenseVoice-Small ONNX: https://huggingface.co/OpenVoiceOS/sensevoice-small-onnx
- Qwen2.5-Omni-7B: https://huggingface.co/Qwen/Qwen2.5-Omni-7B
- Qwen2-Audio-7B-Instruct: https://huggingface.co/Qwen/Qwen2-Audio-7B-Instruct
- Qwen3-TTS README: https://raw.githubusercontent.com/QwenLM/Qwen3-TTS/HEAD/README.md
- NVIDIA Audio2Emotion-v2.2: https://huggingface.co/nvidia/Audio2Emotion-v2.2
