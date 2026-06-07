#!/usr/bin/env python3
"""Scan downloaded PlanD OBJ tiles and print per-axis vertex ranges next to each
tile's HK1980 CSV extents — used once to deduce the local-origin/axis convention
before finalizing the Unity importer math.
"""
import json
import os

UNPACK = os.path.join(os.path.expanduser("~"), "Downloads", "pland_tiles", "unpacked")

with open(os.path.join(UNPACK, "offsets.json"), encoding="utf-8") as f:
    offsets = {t["name"]: t for t in json.load(f)["tiles"]}

for tile in sorted(os.listdir(UNPACK)):
    tdir = os.path.join(UNPACK, tile)
    if not os.path.isdir(tdir):
        continue
    objs = [f for f in os.listdir(tdir) if f.lower().endswith(".obj")]
    if not objs:
        continue
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    n = 0
    with open(os.path.join(tdir, objs[0]), encoding="ascii", errors="ignore") as f:
        for line in f:
            if not line.startswith("v "):
                continue
            parts = line.split()
            for i in range(3):
                val = float(parts[1 + i])
                if val < lo[i]:
                    lo[i] = val
                if val > hi[i]:
                    hi[i] = val
            n += 1
    o = offsets.get(tile, {})
    print(f"{tile:18s} v={n:7d}  "
          f"x[{lo[0]:9.1f},{hi[0]:9.1f}] y[{lo[1]:10.1f},{hi[1]:10.1f}] z[{lo[2]:7.1f},{hi[2]:7.1f}]  "
          f"| CSV E[{o.get('min_x', 0):.0f},{o.get('max_x', 0):.0f}] N[{o.get('min_y', 0):.0f},{o.get('max_y', 0):.0f}]")
