# Heal v2. v1 failed because the global brightest pixel IS the mast (specular white
# paint), so the "sun protection" disc shielded it. Strategy now:
#  1) restore pristine file from backup
#  2) hard pass: unconditional thin-structure removal in the mast column band x=[2405,2460]
#  3) global pass: remove thin bright structures (guy wires) ONLY over blue-sky background
#     (med_B > 1.12*med_R) -> cloud edges and the hazy sun are untouched; wires over
#     clouds are invisible anyway.
import shutil
import cv2
import numpy as np

SRC = r"D:\Proiecte\Munich Hackathon\DroneDetection\Assets\HongKongPhoto\Sky\kloofendal_48d_partly_cloudy_puresky_4k.hdr"
BAK = r"D:\Proiecte\Munich Hackathon\DroneDetection\Tools\kloofendal_4k_original_backup.hdr"

shutil.copyfile(BAK, SRC)  # start pristine
img = cv2.imread(SRC, cv2.IMREAD_UNCHANGED)
h, w = img.shape[:2]

# ---- pass 1: mast band, unconditional ----
x0, x1 = 2395, 2530
band = img[:, x0:x1].copy()
kb = np.ones((1, 41), np.uint8)
opened_b = np.dstack([cv2.morphologyEx(img[:, max(0, x0 - 60):x1 + 60, c], cv2.MORPH_OPEN, kb) for c in range(3)])
ob = opened_b[:, 60:60 + (x1 - x0)]
db = (band - ob).max(axis=2)
mb = (db > 0.04)
mb = cv2.dilate(mb.astype(np.uint8), np.ones((3, 5), np.uint8), iterations=2).astype(bool)
band[mb] = ob[mb]
img[:, x0:x1] = band
n1 = int(mb.sum())

# ---- pass 2: wires over blue sky, global ----
med = np.dstack([cv2.medianBlur(img[:, :, c], 5) for c in range(3)])
delta = (img - med).max(axis=2)
bluebg = med[:, :, 0] > 1.12 * med[:, :, 2]  # BGR: B > 1.12*R
mask = (delta > 0.06) & bluebg
mask = cv2.dilate(mask.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=1).astype(bool) & bluebg
img[mask] = med[mask]
n2 = int(mask.sum())

print(f"mast_band_px={n1} wire_px={n2}")

# in-file verification: rerun the column detector
k = np.ones((1, 31), np.uint8)
opened = np.dstack([cv2.morphologyEx(img[:, :, c], cv2.MORPH_OPEN, k) for c in range(3)])
d2 = (img - opened).max(axis=2)
colscore = (d2[:1300, :] > 0.08).sum(axis=0)
top = np.argsort(colscore)[-10:][::-1]
print("post-heal top columns:", [(int(x), int(colscore[x])) for x in top])

# tonemapped eyeball crops around the mast + a wire region
def tone(a):
    return (np.clip(a / max(a.max() * 0.25, 1e-3), 0, 1) ** (1 / 2.2) * 255).astype(np.uint8)

cv2.imwrite(r"D:\Proiecte\Munich Hackathon\DroneDetection\Captures\_heal2_mast_crop.png", tone(img[0:1100, 2300:2560]))
ok = cv2.imwrite(SRC, img)
print("written:", ok)
