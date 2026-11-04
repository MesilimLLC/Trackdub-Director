# DOD Sgt. Harris sign localisation pass (2026-11-04)

Target: the "THE CULINARY OUTPOST" food-truck logo in the Sgt. 1st Class
September Harris interview, using Trackdub Director's existing visual
localisation and static-shot lanes. No code changes.

- Reference frame: `reference-095.jpg` (95 s, 1920x1080)
- Box (x1,y1,x2,y2): 420,420,820,635
- Source text: "THE CULINARY OUTPOST"; target: "EL PUESTO CULINARIO"
- One `muse-image-1.0` call via `/api/visual-localisation`
  (`localisation-report.json`): status Localised, outside-box diff 0,
  28,077 tokens, 19.6 s wall.
- Static-shot gate: a first window to 116.7 s was refused only by the final
  black frames after the shot (116.216 s on). Re-measured/composited over
  92.5-116.18 s: 709 frames, SSIM min 0.9838, mean 0.9862, outside-box diff
  max 0. Output: `DOD_111078203-sign-es.mp4`
  (full video re-encoded, audio copied; `sign-shot-report.json`).

## Visual verdict (frame inspection at 95/105/114 s)

Partial pass. The repaint is legible and stable across the interview and
reads "EL PUESTO CULINARIO" in a matching style, but it is not clean:
the small original "THE" survives above "EL", the chef-hat graphic is
restyled, and the repainted box has a visibly darker black background than
the truck panel, leaving a faint rectangular seam. Only this one logo was
replaced; menu screens, flags, shirt, and cap text remain English. The
image-edit template wording still says "on the shop door sign" (image-edit-v1,
probe-verified for the VOA sign), which may contribute to the leftover "THE".

Conclusion: the existing lane can hold a Spanish repaint through a locked-off
real-world interview shot. Text fidelity (leftover source word) and
background matching are the quality gaps, not temporal stability.
