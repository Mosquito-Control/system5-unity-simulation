#!/usr/bin/env python3
"""Download PlanD 3D photo-realistic OBJ tiles covering the Victoria Harbour demo zone.

Parses the GridIdx_OBJ.csv (tab-delimited), selects tiles intersecting the AOI,
downloads + unzips each into ~/Downloads/pland_tiles/unpacked/<GRID_NAME>/ and writes
offsets.json mapping tile name -> HK1980 MIN_X/MIN_Y (the per-tile world translation,
since OBJ vertices are tile-local).

Usage: python download_pland_tiles.py [path-to-GridIdx_OBJ.csv]
Attribution: 3D Photo-realistic Model (c) Government of HKSAR (Planning Department), via DATA.GOV.HK.
"""
import csv
import io
import json
import os
import sys
import urllib.request
import zipfile

IDX_URL = "https://pdmap.pland.gov.hk/PLANDWEB/public/3d_photo_realistic_models/Metadata/GridIdx_OBJ.csv"
# Demo zone: greater Tsim Sha Tsui — every Kowloon tile (Tile_+E_+N grid, shared regional
# frame) in columns +004..+014, rows +000..+003. Island tiles (tile_C_R) excluded: different
# production batch, unverified frame alignment.
def in_pick(name):
    # "Tile_+007_+000" -> col +7, row +0 ; only positive quadrant tiles qualify
    if not name.startswith("Tile_+") or len(name) < 14 or name[10] != "+":
        return False
    try:
        col = int(name[6:9])
        row = int(name[11:])
    except ValueError:
        return False
    return 4 <= col <= 14 and 0 <= row <= 3

OUT = os.path.join(os.path.expanduser("~"), "Downloads", "pland_tiles")
UNPACK = os.path.join(OUT, "unpacked")


def decode_index(data):
    # PlanD publishes the index as UTF-16 LE with BOM (Chinese location columns)
    for enc in ("utf-16", "utf-8-sig"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace")


def load_index(path_or_none):
    if path_or_none and os.path.exists(path_or_none):
        with open(path_or_none, "rb") as f:
            return decode_index(f.read())
    print("fetching index:", IDX_URL)
    with urllib.request.urlopen(IDX_URL, timeout=60) as r:
        return decode_index(r.read())


def main():
    raw = load_index(sys.argv[1] if len(sys.argv) > 1 else None)
    rows = list(csv.DictReader(io.StringIO(raw), delimiter="\t"))
    print(f"index rows: {len(rows)}")

    picks = []
    for row in rows:
        try:
            minx, miny = float(row["MIN_X"]), float(row["MIN_Y"])
            maxx, maxy = float(row["MAX_X"]), float(row["MAX_Y"])
        except (KeyError, ValueError):
            continue
        if not in_pick(row["GRID_NAME"]):
            continue
        picks.append({"name": row["GRID_NAME"], "url": row["FILE_URL"],
                      "min_x": minx, "min_y": miny, "max_x": maxx, "max_y": maxy,
                      "loc": row.get("LOC_EN", "")})

    print(f"AOI tiles: {len(picks)}")
    for p in picks:
        print(f"  {p['name']:18s} E{p['min_x']:.0f}-{p['max_x']:.0f} N{p['min_y']:.0f}-{p['max_y']:.0f}  {p['loc']}")
    if not picks:
        sys.exit(1)

    os.makedirs(UNPACK, exist_ok=True)
    offsets = []  # JsonUtility-friendly: {"tiles":[{name,min_x,min_y,max_x,max_y},...]}
    done = failed = 0
    for p in picks:
        tile_dir = os.path.join(UNPACK, p["name"])
        offsets.append({"name": p["name"], "min_x": p["min_x"], "min_y": p["min_y"],
                        "max_x": p["max_x"], "max_y": p["max_y"]})
        if os.path.isdir(tile_dir) and any(f.lower().endswith(".obj") for f in os.listdir(tile_dir)):
            print(f"  [skip] {p['name']} (already unpacked)")
            done += 1
            continue
        try:
            print(f"  [get ] {p['name']} <- {p['url']}")
            with urllib.request.urlopen(p["url"], timeout=300) as r:
                data = r.read()
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                os.makedirs(tile_dir, exist_ok=True)
                for member in z.namelist():
                    fn = os.path.basename(member)
                    if not fn:
                        continue
                    with z.open(member) as src, open(os.path.join(tile_dir, fn), "wb") as dst:
                        dst.write(src.read())
            done += 1
            print(f"         ok ({len(data)//(1024*1024)} MB)")
        except Exception as e:  # noqa: BLE001 - report and continue
            failed += 1
            print(f"         FAILED: {e}")

    # ground truth: each tile zip carries config.json with model_transform — vertex + its
    # translation column = absolute HK1980 (E, N, height). Use that, not the CSV extents.
    for o in offsets:
        cfg_path = os.path.join(UNPACK, o["name"], "config.json")
        if os.path.exists(cfg_path):
            with open(cfg_path, encoding="utf-8") as f:
                mt = json.load(f)["model_transform"]
            o["tx"], o["ty"], o["tz"] = mt[0][3], mt[1][3], mt[2][3]
        else:
            o["tx"], o["ty"], o["tz"] = o["min_x"], o["min_y"], 0.0
            print(f"  WARN no config.json for {o['name']} — using CSV mins")
    with open(os.path.join(UNPACK, "offsets.json"), "w", encoding="utf-8") as f:
        json.dump({"tiles": offsets}, f, indent=1)
    print(f"done: {done} ok, {failed} failed -> {UNPACK}")
    print("offsets.json written (tile -> model_transform translation tx/ty/tz)")


if __name__ == "__main__":
    main()
