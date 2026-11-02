# Glimmer recovery run notes

- Direction: en -> es
- Glimmer model: meta-models--muse-glimmer-30b (reasoning_effort low)
- Meaning gate: muse-spark-1.1 (template meaning-v1)
- Attempt cap per item: 3
- Recovery (per attempt): 4/4 items recovered.
- Recovery (per distinct line): 3/3 lines recovered (a line counts as recovered if any of its refused attempts recovered; reported separately so the rate is not double-counted).
- Budget: Glimmer calls 8/45, Meta calls 4/35.

## Route (read from env only; recorded here as observed values)

The client reads GLIMMER_BASE_URL / GLIMMER_API_KEY / GLIMMER_MODEL and the Meta key
from the environment only; nothing below is hardcoded into a code path. GLIMMER_BASE_URL
must be the OpenAI-compatible base ending in `/openai/v1` (the client appends
`/chat/completions` itself; passing the full `.../chat/completions` URL would double the path).
The Glimmer model string recorded for this run was `meta-models--muse-glimmer-30b`
(reasoning_effort low). The pre-flight probe result is
recorded below; the run proceeded to the loop only after the probe returned
{"ok": true} from the requested Glimmer model.

### Pre-flight probe

- Endpoint host: `trackdub-6788-resource.services.ai.azure.com` (the key is never recorded)
- Requested model: `meta-models--muse-glimmer-30b`
- Returned model: `meta-models--muse-glimmer-30b`
- Usage: prompt=68 completion=163 total=231
- Wall seconds: 7.094

## Leakage rule

Each item is built from exactly ONE refused attempt record. The agent's input is only
the source line, the window and tolerance, the recorded gate failure reasons for that
attempt, and any captured scene context. No sibling attempt's text for the same segment
(later revision, retry, or accepted candidate) is ever included, and the set is built
from the captured QA reports (gate outputs), never from the seeded meaning-eval labels.

## Per-item outcomes

- `voa10f-002` att#2 (samples/voa10-full/qa-report-pass1.json, Meaning, window 0.50s): Recovered -> "¡Ashley!" [1 attempt(s)]
- `seg-006` att#2 (samples/meaning-eval/qa-report-pressure-70.json, Meaning, window 1.12s): Recovered -> "¿Dónde rodar mañ?" [2 attempt(s)]
- `seg-006` att#3 (samples/meaning-eval/qa-report-pressure-70.json, Meaning, window 1.12s): Recovered -> "¿Dónde rodamos mñ?" [2 attempt(s)]
- `seg-007` att#2 (samples/meaning-eval/qa-report-pressure-70.json, Meaning, window 0.84s): Recovered -> "Gracias venir" [2 attempt(s)]

Disclosure: two of the three recovered lines are telegraphic. The seg-006 winners clip mañana to the non-word forms "mañ" and "mñ", and the seg-007 winner "Gracias venir" is telegram-speak. The meaning gate passed all of them as preserving intent. The 4 of 4 recovery figure should always be cited with this disclosure.
