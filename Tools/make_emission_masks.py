#!/usr/bin/env python3
"""Generate window emission masks from the HK wall color textures.

Heuristic: window glass reads darker / bluer than the surrounding facade in these
textures, so we threshold on luminance (+ slight blue bias), clean up with
morphology, and save:
  Assets/HongKong/Building_Texture/Wall{i}_Emission.png   (mask * warm tint)
  Tools/mask_previews/wall{i}_preview.jpg                 (color | mask side-by-side)
Tune PER_WALL_Q if a specific wall over/under-selects.
"""
import os

import cv2
import numpy as np

ROOT = os.path.join(os.path.dirname(__file__), "..")
TEX = os.path.join(ROOT, "Assets", "HongKong", "Building_Texture")
PREV = os.path.join(os.path.dirname(__file__), "mask_previews")
os.makedirs(PREV, exist_ok=True)

# luminance quantile below which a pixel counts as window glass (per wall, tweakable)
PER_WALL_Q = {1: 0.35, 2: 0.35, 3: 0.35, 4: 0.35, 5: 0.65, 6: 0.35}
# wall5 has bright glass on white panels -> select BRIGHT bluish pixels instead
BRIGHT_MODE = {5}
WARM = np.array([90, 190, 255], dtype=np.float32) / 255.0  # BGR warm window light

for i in range(1, 7):
    src = os.path.join(TEX, f"Wall{i}_Color.png")
    img = cv2.imread(src, cv2.IMREAD_COLOR)
    if img is None:
        print(f"wall{i}: MISSING {src}")
        continue

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    b, g, r = [img[..., c].astype(np.float32) for c in range(3)]
    blueness = np.clip((b - r) / 255.0 + 0.5, 0, 1)  # glass tends slightly blue

    thresh = np.quantile(gray, PER_WALL_Q[i])
    if i in BRIGHT_MODE:
        mask = ((gray > thresh) & (blueness > 0.48)).astype(np.uint8) * 255
    else:
        mask = ((gray < thresh) & (blueness > 0.45)).astype(np.uint8) * 255

    # clean: drop speckles, keep window-sized blobs
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))

    # random subset of windows lit (not every office works late) — stable seed
    rng = np.random.default_rng(42 + i)
    n_labels, labels = cv2.connectedComponents(mask)
    lit = rng.random(n_labels) < 0.65
    lit[0] = False
    mask = (lit[labels] * 255).astype(np.uint8)

    m = (mask.astype(np.float32) / 255.0)[..., None]
    emission = (m * WARM[None, None, :] * 255).astype(np.uint8)
    out = os.path.join(TEX, f"Wall{i}_Emission.png")
    cv2.imwrite(out, emission)

    h = min(512, img.shape[0])
    scale = h / img.shape[0]
    a = cv2.resize(img, None, fx=scale, fy=scale)
    bb = cv2.resize(emission, None, fx=scale, fy=scale)
    cv2.imwrite(os.path.join(PREV, f"wall{i}_preview.jpg"), np.hstack([a, bb]))
    pct = 100.0 * (mask > 0).mean()
    print(f"wall{i}: lit-window coverage {pct:.1f}% -> {out}")
