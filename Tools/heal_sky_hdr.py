# Remove thin bright structures (radio mast + guy wires) baked into the HDRI sky pano.
# They render as dashed "path-like" lines in any west-facing camera. Method: horizontal
# morphological opening estimates the sky background; pixels that stick out above it by
# both an absolute and relative margin are thin structures -> replace with background.
# Clouds survive (wide + soft), the sun disc is explicitly protected.
import shutil
import cv2
import numpy as np

SRC = r"D:\Proiecte\Munich Hackathon\DroneDetection\Assets\HongKongPhoto\Sky\kloofendal_48d_partly_cloudy_puresky_4k.hdr"
BAK = r"D:\Proiecte\Munich Hackathon\DroneDetection\Tools\kloofendal_4k_original_backup.hdr"

shutil.copyfile(SRC, BAK)
img = cv2.imread(SRC, cv2.IMREAD_UNCHANGED)  # float32 BGR, linear
assert img is not None and img.dtype == np.float32, (type(img), getattr(img, "dtype", None))
h, w = img.shape[:2]

# horizontal opening per channel: anything thinner than ~11 px horizontally is flattened
kernel = np.ones((1, 11), np.uint8)
opened = np.dstack([cv2.morphologyEx(img[:, :, c], cv2.MORPH_OPEN, kernel) for c in range(3)])

delta = (img - opened).max(axis=2)
base = opened.max(axis=2)
mask = (delta > 0.10) & (delta > 0.5 * base)

# protect the sun disc + glow (brightest spot, generous radius)
lum = img.mean(axis=2)
sy, sx = np.unravel_index(np.argmax(lum), lum.shape)
yy, xx = np.ogrid[:h, :w]
sun = (yy - sy) ** 2 + (np.minimum(np.abs(xx - sx), w - np.abs(xx - sx))) ** 2 < 120 ** 2
mask &= ~sun

mask = cv2.dilate(mask.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=1).astype(bool)
img[mask] = opened[mask]

n = int(mask.sum())
print(f"size={w}x{h} healed_px={n} ({100.0 * n / (w * h):.4f}%) sun_at=({sx},{sy})")
ok = cv2.imwrite(SRC, img)
print("written:", ok)
