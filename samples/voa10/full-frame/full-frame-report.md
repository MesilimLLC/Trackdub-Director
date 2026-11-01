# Full-frame redraw capture — region SSIM comparison

Evidence-only capture. One live call: `muse-image-1.0` via
`POST https://api.meta.ai/v1/responses` (image-edit-v1 shape, the
localisation stage's instruction verbatim) applied to the FULL 1920x1080
hero frame instead of the sign crop. Budget: 1 of 1 spent. No other live
calls (no Meta text/vision, no ElevenLabs).

## Call

- Requested / returned model: `muse-image-1.0` / `muse-image-1.0`
  (`response_id resp_6ae73cc3b8a73986c153419c`, status `completed`, HTTP 200).
- Instruction:
  `replace the text "COFFEE SHOP" on the shop door sign with "CAFETERÍA", preserve everything else in the image exactly`
- Usage: prompt 11773 / completion 550 / total **12323** tokens
  (Oct 31 probe estimate for this exact call: about 12505).
- Wall time: 11.22 s.
- Returned: WEBP, **2096x1184**, 275684 bytes
  (`full-frame-0242-raw.webp`, byte-for-byte as returned).
  The size/format mismatch against the 1920x1080 source is itself a finding.

## Input verification

- Source `samples/voa10/frames/hero-0242.jpg`, 1920x1080, 211450 bytes,
  clip-relative 6.52 s. SHA-256 verified before the call:
  `2f3e4cbba9adcb4d2afdd15eca1cfeb102ff8bd0b19e849e97ae05b764d64dd7`.
- Discrepancy on record: the original prompt and `samples/voa10/NOTES.md`
  line 54 carry a truncated 63-char value (this hash minus the final `7`).
  The 64-char value above is authoritative. NOTES.md was NOT edited here;
  its fix is a separate freeze pass 2 docs item.

## Normalization

- `full-frame-0242-1920x1080.png`: raw return resampled to 1920x1080 with
  `PIL.Image.LANCZOS`, saved as PNG (1449224 bytes).
- Every pixel comparison below runs on this **resampled** image vs the
  source — not on the raw 2096x1184 WEBP.

## Method

- SSIM: standard Wang/Bovik Gaussian-window SSIM (11x11, sigma 1.5,
  K1=0.01, K2=0.03, L=255), per colour channel then averaged, via
  `scipy.ndimage.gaussian_filter` (truncate 3.0, mode reflect). Rectangular
  regions are cropped first; the outside-sign region averages the
  whole-image SSIM map over the mask. Library versions: PIL 12.3.0,
  numpy 2.5.2, scipy 1.18.0 (no scikit-image on this machine).
- mean-abs-diff: mean over HxWx3 uint8 pixels of `abs(a-b)`.
- Regions (1920x1080): sign box (449,145)-(751,534) from the localisation
  manifest; full door panel (415,140)-(705,985) and VOA watermark approx
  (1380,170)-(1830,230) from NOTES.md section 2; face (1085,435)-(1240,625)
  derived by the author's visual inspection of the hero frame (the on-screen
  speaker: woman in knit beanie holding a phone) — not from a detector or
  ground truth; outside-sign = whole frame minus the sign box.

## Results

| region | full-frame resampled vs source (SSIM / mean-abs) | crop composite vs source (SSIM / mean-abs) |
|---|---|---|
| sign box | 0.7526 / 15.57 | 0.7877 / 14.92 |
| door panel | 0.7547 / 11.87 | 0.9044 / 6.84 |
| watermark | 0.7908 / 11.75 | 1.0000 / 0.00 |
| face (visual inspection) | 0.7061 / 12.55 | 1.0000 / 0.00 |
| outside sign box | 0.8208 / 6.55 | 0.9998 / 0.00 |

- Composite outside-box diff: **exactly 0** — 0 differing pixels,
  max abs diff 0.0 over the whole frame minus the sign box. Verified, not
  assumed. (Its outside-box SSIM reads 0.9998 rather than 1.0 only because
  the Gaussian SSIM window bleeds across the box edge; the integer pixel
  check is the exact verdict.)
- Heatmaps (mean-abs diff x8, factor burned into each image's label bar):
  `heatmap-full-frame-vs-source.png`,
  `heatmap-composite-vs-source.png` (each 1920x1110: 1920x1080 map + 30px
  label bar).

## Verdict, in plain words

The full-frame call did repaint the sign to CAFETERÍA, but it redrew the
whole picture: every region moved (mean abs diff 6.5 outside the sign,
11.8–15.6 across the regions of interest; SSIM 0.71–0.82, nowhere near 1).
Face, STORE HOURS text, watermark and background all shifted, and the model
returned a different size (2096x1184 WEBP for a 1920x1080 source). The crop
composite is byte-identical to the source outside the sign box and changes
only the sign interior. That is the point of the comparison:
crop-edit-composite localises the repaint; full-frame regeneration does not.

## Files (all inside `samples/voa10/full-frame/`, nowhere else)

- `full-frame-0242-raw.webp` — raw return, byte-for-byte.
- `full-frame-0242-1920x1080.png` — LANCZOS resample for measurement.
- `heatmap-full-frame-vs-source.png`, `heatmap-composite-vs-source.png`.
- `full-frame-report.json`, `full-frame-report.md` (this file).
- `analyze_full_frame.py` — standalone analysis script; usage line at top
  (`python samples/voa10/full-frame/analyze_full_frame.py` from repo root).
