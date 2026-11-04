# Computed-speed retake — experiment record (2026-11-02)

## Phase A: calibration probe (executed)

6 voa10 winning lines x 2 cast voices (Alice, Bella) x speeds 1.0/1.05/1.10/1.15/1.20
= 60 syntheses, 2,200 ElevenLabs characters billed. Durations measured from the
returned audio with the audition gate's own measurement (PCM seconds), never
provider-reported duration. Raw measurements: phase-a-measurements.json.

| Voice | Line | 1.0 s | Realized ratio error vs 1/speed (max, cells > 0.05) | Monotonic |
|---|---|---|---|---|
| Alice | voa10-001 | 1.254 | max 0.168, 3/4 over | NO |
| Alice | voa10-002 | 2.833 | max 0.081, 3/4 over | NO |
| Alice | voa10-003 | 4.040 | max 0.144, 3/4 over | yes |
| Alice | voa10-004 | 3.111 | max 0.087, 2/4 over | NO |
| Alice | voa10-005 | 2.183 | max 0.082, 2/4 over | yes |
| Alice | voa10-006 | 1.858 | max 0.158, 4/4 over | yes |
| Bella | voa10-001 | 1.022 | max 0.045, 0/4 over | yes |
| Bella | voa10-002 | 2.926 | max 0.131, 3/4 over | NO |
| Bella | voa10-003 | 3.762 | max 0.129, 2/4 over | NO |
| Bella | voa10-004 | 2.740 | max 0.074, 2/4 over | NO |
| Bella | voa10-005 | 1.858 | max 0.198, 4/4 over | NO |
| Bella | voa10-006 | 1.579 | max 0.099, 2/4 over | yes |

Across 48 speed cells: 18/48 within plus or minus 0.05 of the
ideal ratio; median absolute error 0.064. 11/12 lines have a
worst cell beyond tolerance; 7/12 lines are non-monotonic in requested speed
(a higher requested speed sometimes produced a longer take than a lower one, e.g.
identical durations at three adjacent speed settings on one Bella line). Because every
synthesis is a fresh generation, step-to-step generation variance is the same order as
the speed effect itself at these factors.

**Kill criterion (spec): response is non-monotonic or realized error exceeds plus or
minus 0.05 in ratio for most lines. Both hold. Phase A is a measured negative result.**

## Phases B and C: not run

Each phase gates the next. Phase B (rescue rate) and Phase C (human naturalness)
were not executed, and no further synthesis was billed after the kill criterion
was met. There are therefore no speed-adjusted winning takes to mix into a
naturalness set, and no cap can be set from human flags.

## Adopt decision

Not adopted. All three adopt criteria cannot hold: the first (orderly response,
monotonic, error within tolerance) fails on this data. The shipped behavior stays
revision-or-refusal: the computed-speed retake remains in code behind
`speed_retake.enabled: false` (default), with its unit tests, manifest labeling,
calibration sidecar, and verify-only surfacing in place should the provider's
speed response become orderly enough to re-measure. Claim wording is adjusted:
computed-speed audio is never claimed by a shipped run, but a segment with no
winner can still get one fixed-factor `SpeedVariant` synthesized at 1.15 (see
`AddSpeedVariantAsync`) when a meaning-passing take exists and the per-segment
cap allows it — the audition-v4 run records such a take for voa10-005.
