# Emotion tag pass-through A/B (2026-11-04)

Same gated Spanish text, same cast voice, model `eleven_v4`. The only variable
is the tag: `-mapped` uses the old keyword wordmap, `-passthrough` sends the
extracted labels in their own words as one grouped bracket. Winning texts,
voices, and scene labels come from
`samples/voa10/pipeline-run/e2e-v4-scene-2026-11-02.json`. Full details in
`ab-manifest.json`. 8 syntheses, 437 ElevenLabs characters total.

Listen in pairs. Question per pair: does the pass-through take read as the
label says, better or worse than the mapped take — and is anything spoken
aloud that should not be?

- voa10-001 (Alice): [cheerfully] vs [cheerful and animated] — "Hola Ashley."
- voa10-002 (Bella): [briskly] vs [animated and expressive, brisk with emphasis]
- voa10-003 (Alice): [cheerfully] vs [cheerful, conversational]
- voa10-006 (Bella): no tag (old map dropped the label) vs [engaged and conversational]

Durations are measured from the written WAV bytes and recorded in the
manifest. Nothing here is gated or cast; this is a listening check only.

Verdict (Michael, listening, 2026-11-04): the pass-through takes read as
their labels say, and sound better than the mapped takes. Label pass-through
is the adopted behavior; the keyword wordmap stays retired.
