"""Full-frame redraw evidence analysis (standalone; no live calls, no network).

Usage: from the repo root run: python samples/voa10/full-frame/analyze_full_frame.py

Reads samples/voa10/frames/hero-0242.jpg (source) and
samples/voa10/frames/localised/hero-0242-cafeteria.png (crop composite),
plus samples/voa10/full-frame/full-frame-0242-raw.webp (the single live
call return). Writes into this folder: full-frame-0242-1920x1080.png
(PIL LANCZOS resample), heatmap-full-frame-vs-source.png and
heatmap-composite-vs-source.png (mean-abs diff x8, labelled). Prints a
metrics JSON to stdout: per-region SSIM and mean-abs-diff for both
comparisons, and the composite outside-box integer diff (must be 0).

SSIM method: standard Wang/Bovik Gaussian-window SSIM (11x11, sigma 1.5,
K1=0.01, K2=0.03, L=255) computed per colour channel and averaged, via
scipy.ndimage.gaussian_filter (truncate 3.0, mode reflect); rectangular
regions are cropped first, the outside-sign region averages the whole-image
SSIM map over the mask. numpy/scipy/PIL versions are printed in the output.
"""
import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import gaussian_filter

import PIL
import scipy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
SRC = os.path.join(ROOT, "samples", "voa10", "frames", "hero-0242.jpg")
COMPOSITE = os.path.join(
    ROOT, "samples", "voa10", "frames", "localised", "hero-0242-cafeteria.png")
RAW = os.path.join(HERE, "full-frame-0242-raw.webp")
RESAMPLED = os.path.join(HERE, "full-frame-0242-1920x1080.png")
HEAT_FF = os.path.join(HERE, "heatmap-full-frame-vs-source.png")
HEAT_COMP = os.path.join(HERE, "heatmap-composite-vs-source.png")

AMPLIFY = 8  # fixed heatmap amplification factor, also burned into each heatmap

# 1920x1080 regions. Face box derived by the author's visual inspection of the
# hero frame (on-screen speaker: woman in knit beanie holding a phone to her
# left ear); it is not from a detector or ground truth.
REGIONS = {
    "sign_box": (449, 145, 751, 534),
    "door_panel": (415, 140, 705, 985),
    "watermark": (1380, 170, 1830, 230),
    "face_visual_inspection": (1085, 435, 1240, 625),
}


def ssim_map(a, b):
    """Per-pixel SSIM map averaged over the 3 colour channels."""
    a = a.astype(np.float64)
    b = b.astype(np.float64)
    K1, K2, L = 0.01, 0.03, 255.0
    C1, C2 = (K1 * L) ** 2, (K2 * L) ** 2
    maps = []
    for c in range(3):
        x, y = a[:, :, c], b[:, :, c]
        mu_x = gaussian_filter(x, 1.5, truncate=3.0, mode="reflect")
        mu_y = gaussian_filter(y, 1.5, truncate=3.0, mode="reflect")
        sig_xx = gaussian_filter(x * x, 1.5, truncate=3.0, mode="reflect") - mu_x * mu_x
        sig_yy = gaussian_filter(y * y, 1.5, truncate=3.0, mode="reflect") - mu_y * mu_y
        sig_xy = gaussian_filter(x * y, 1.5, truncate=3.0, mode="reflect") - mu_x * mu_y
        num = (2 * mu_x * mu_y + C1) * (2 * sig_xy + C2)
        den = (mu_x * mu_x + mu_y * mu_y + C1) * (sig_xx + sig_yy + C2)
        maps.append(num / den)
    return float("nan"), np.mean(maps, axis=0)


def crop(arr, box):
    x1, y1, x2, y2 = box
    return arr[y1:y2, x1:x2]


def region_stats(a, b, box):
    ca, cb = crop(a, box), crop(b, box)
    _, m = ssim_map(ca, cb)
    return float(np.mean(m)), float(np.mean(np.abs(ca.astype(np.int16) - cb.astype(np.int16))))


def heatmap(diff, caption):
    amp = np.clip(diff * AMPLIFY, 0, 255).astype(np.uint8)
    img = Image.fromarray(amp, mode="L").convert("RGB")
    bar = Image.new("RGB", (img.width, 30), (255, 255, 255))
    ImageDraw.Draw(bar).text((10, 8), caption, fill=(0, 0, 0))
    out = Image.new("RGB", (img.width, img.height + 30), (255, 255, 255))
    out.paste(img, (0, 0))
    out.paste(bar, (0, img.height))
    return out


def main():
    """Run the full-frame vs source vs composite comparison; print metrics.
    Returns 0 on success, 2 on source hash mismatch, and 1 when the crop
    composite changes pixels outside the sign box."""
    src_bytes = open(SRC, "rb").read()
    if hashlib.sha256(src_bytes).hexdigest() != (
            "2f3e4cbba9adcb4d2afdd15eca1cfeb102ff8bd0b19e849e97ae05b764d64dd7"):
        print("source hash mismatch; refusing to measure", file=sys.stderr)
        return 2
    src = np.asarray(Image.open(SRC).convert("RGB"))
    comp = np.asarray(Image.open(COMPOSITE).convert("RGB"))
    raw = Image.open(RAW).convert("RGB")
    assert src.shape == (1080, 1920, 3), src.shape
    assert comp.shape == (1080, 1920, 3), comp.shape

    ff = raw.resize((1920, 1080), Image.LANCZOS)
    ff.save(RESAMPLED)
    ffa = np.asarray(ff)

    metrics = {"comparisons": {}, "library": {
        "method": "Wang/Bovik Gaussian-window SSIM, 11x11 sigma 1.5, K1=0.01 K2=0.03 L=255, per-channel mean; mean-abs-diff over HxWx3 uint8 pixels",
        "PIL": PIL.__version__, "numpy": np.__version__, "scipy": scipy.__version__,
        "heatmap_amplification": AMPLIFY, "resampling": "PIL.Image.LANCZOS"}}
    for name, arr, hpath, hcap in (
            ("full_frame_resampled_vs_source", ffa, HEAT_FF,
             "full-frame resampled vs source, mean-abs-diff x%d" % AMPLIFY),
            ("crop_composite_vs_source", comp, HEAT_COMP,
             "crop composite vs source, mean-abs-diff x%d" % AMPLIFY)):
        table = {}
        for rname, box in REGIONS.items():
            s, m = region_stats(arr, src, box)
            table[rname] = {"ssim": s, "mean_abs_diff": m}
        x1, y1, x2, y2 = REGIONS["sign_box"]
        mask = np.ones(src.shape[:2], bool)
        mask[y1:y2, x1:x2] = False
        _, full_map = ssim_map(arr, src)
        table["outside_sign_box"] = {
            "ssim": float(np.mean(full_map[mask])),
            "mean_abs_diff": float(np.mean(
                np.abs(arr.astype(np.int16) - src.astype(np.int16))[mask]))}
        metrics["comparisons"][name] = table
        heatmap(np.mean(np.abs(arr.astype(np.int16) - src.astype(np.int16)),
                        axis=2), hcap).save(hpath)

    outside = np.abs(comp.astype(np.int16) - src.astype(np.int16))
    x1, y1, x2, y2 = REGIONS["sign_box"]
    outside_mask = _pad(src.shape[:2], x1, y1, x2, y2)
    metrics["composite_outside_box_integer_check"] = {
        "differing_pixels": int(np.count_nonzero(
            np.any(outside, axis=2) & outside_mask)),
        "max_abs_diff_outside": float(np.max(outside[outside_mask]))}
    print(json.dumps(metrics, indent=1))
    if metrics["composite_outside_box_integer_check"]["differing_pixels"] != 0:
        print("composite changed pixels outside the sign box", file=sys.stderr)
        return 1
    return 0


def _pad(shape, x1, y1, x2, y2):
    m = np.ones(shape, bool)
    m[y1:y2, x1:x2] = False
    return m


if __name__ == "__main__":
    sys.exit(main() or 0)
