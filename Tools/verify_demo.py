#!/usr/bin/env python3
"""Demo acceptance check: sample ground-truth labels for N seconds and verify
the v0.3-demo criteria: 3 drones flying, all drone_ids detected, multi-camera
coverage, and a high always-detected rate.

Usage: python verify_demo.py [seconds] [port]   (defaults: 20s, 9870)
Exit code 0 = PASS, 1 = FAIL/timeout.
"""
import json
import socket
import sys
import time
from collections import Counter

secs = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
port = int(sys.argv[2]) if len(sys.argv) > 2 else 9870

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", port))
s.settimeout(10)

frames = 0
det_by_cam = Counter()
det_by_drone = Counter()
cams_with_det_per_frame = []
frames_with_det = 0
first_pos = {}
last_pos = {}
drone_ids_seen = set()
t_end = time.time() + secs

try:
    while time.time() < t_end:
        data = s.recvfrom(65535)[0]
        p = json.loads(data.decode("utf-8"))
        frames += 1
        for i, d in enumerate(p["drones"]):
            drone_ids_seen.add(i)
            last_pos[i] = d["pos_w"]
            if i not in first_pos:
                first_pos[i] = d["pos_w"]
        cams_det = 0
        any_det = False
        for c in p["cameras"]:
            n = len(c["detections"])
            if n:
                det_by_cam[c["name"]] += n
                cams_det += 1
                any_det = True
                for det in c["detections"]:
                    det_by_drone[det.get("drone_id", 0)] += 1
        cams_with_det_per_frame.append(cams_det)
        if any_det:
            frames_with_det += 1
except socket.timeout:
    print(f"TIMEOUT: no labels on :{port} within 10s (sim not running or labels disabled)")
    sys.exit(1)

if frames == 0:
    print("FAIL: zero frames sampled")
    sys.exit(1)

moved = {i: sum((a - b) ** 2 for a, b in zip(first_pos[i], last_pos[i])) ** 0.5
         for i in first_pos}
pct_det = 100.0 * frames_with_det / frames
avg_cams = sum(cams_with_det_per_frame) / frames
multi_cam_frames = sum(1 for n in cams_with_det_per_frame if n >= 2)
pct_multi = 100.0 * multi_cam_frames / frames

print(f"frames={frames} over {secs:.0f}s")
print(f"drones={len(drone_ids_seen)} ids={sorted(drone_ids_seen)}")
print(f"moved (m): " + ", ".join(f"d{i}={moved[i]:.1f}" for i in sorted(moved)))
print(f"frames with >=1 detection: {pct_det:.1f}%   avg cams detecting/frame: {avg_cams:.2f}   frames with >=2 cams: {pct_multi:.1f}%")
print(f"detections by drone_id: {dict(sorted(det_by_drone.items()))}")
print(f"detections by cam: {dict(sorted(det_by_cam.items(), key=lambda kv: -kv[1]))}")

ok = True
if len(drone_ids_seen) < 3:
    print("FAIL: expected 3 drones in labels"); ok = False
if any(m < 5.0 for m in moved.values()):
    print("FAIL: a drone barely moved (path follower broken?)"); ok = False
if len(det_by_drone) < 3:
    print(f"FAIL: only drone_ids {sorted(det_by_drone)} ever detected"); ok = False
if pct_det < 90.0:
    print(f"FAIL: always-detects rate {pct_det:.1f}% < 90%"); ok = False
if len(det_by_cam) < 2:
    print("FAIL: fewer than 2 cameras ever detected anything"); ok = False

print("PASS" if ok else "FAIL")
sys.exit(0 if ok else 1)
