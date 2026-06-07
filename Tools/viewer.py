#!/usr/bin/env python3
"""Reference viewer for the DroneDetection sim — doubles as protocol documentation.

Consumes exactly what the ML model consumes:
  * Video:  one RTSP H.264 stream per camera   rtsp://<host>:8554/cam0..camN
  * Labels: UDP JSON datagrams on :9870 — one packet per sim frame containing
            3D drone pose(s) + per-camera 2D bboxes + camera intrinsics/extrinsics.
            (schema: SIMULATION_PLAN.md §4.2; coords: image origin top-left)

Shows a 2x4 grid with ground-truth bboxes overlaid and a latency readout.

Usage:
    python viewer.py                          # live window, localhost, 8 cams
    python viewer.py --host 192.168.1.50      # remote sim
    python viewer.py --cams 4                 # fewer cameras
    python viewer.py --save out.jpg           # headless: write grid jpg every 2s
    python viewer.py --delay-ms 300           # label<->video alignment offset
"""

import argparse
import json
import os
import socket
import threading
import time
from collections import deque

# Force TCP for RTSP before OpenCV opens any capture (UDP first-try is flaky on Windows)
os.environ.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp")
# Silence ffmpeg's chatter: 'DESCRIBE failed: 404' (sim not publishing yet) and
# 'decode_slice_header / Missing reference picture' (joining a stream mid-GOP) are
# both expected transients — the viewer auto-retries / recovers at the next keyframe.
os.environ["OPENCV_FFMPEG_LOGLEVEL"] = "-8"

import cv2  # noqa: E402
import numpy as np  # noqa: E402

GRID_COLS = 4
TILE_W, TILE_H = 480, 270  # display tiles (16:9)


class CameraStream(threading.Thread):
    """Reads one RTSP stream, keeps only the latest frame (no backlog)."""

    def __init__(self, url, name):
        super().__init__(daemon=True)
        self.url = url
        self.name = name
        self.frame = None
        self.ok = False
        self.lock = threading.Lock()

    def run(self):
        while True:
            cap = cv2.VideoCapture(self.url, cv2.CAP_FFMPEG)
            cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
            if not cap.isOpened():
                self.ok = False
                time.sleep(2.0)
                continue
            while True:
                ret, frame = cap.read()
                if not ret:
                    break
                with self.lock:
                    self.frame = frame
                    self.ok = True
            cap.release()
            self.ok = False
            time.sleep(1.0)  # stream dropped — retry

    def latest(self):
        with self.lock:
            return (self.frame.copy() if self.frame is not None else None), self.ok


class LabelListener(threading.Thread):
    """Receives label datagrams, keeps a short history for time-aligned lookup."""

    def __init__(self, port):
        super().__init__(daemon=True)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", port))
        self.history = deque(maxlen=120)  # ~8s at 15Hz
        self.lock = threading.Lock()

    def run(self):
        while True:
            data, _ = self.sock.recvfrom(65535)
            try:
                packet = json.loads(data.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
            packet["_recv_ms"] = time.time() * 1000.0
            with self.lock:
                self.history.append(packet)

    def at(self, target_ms):
        """Packet whose sender timestamp is closest to target_ms (None if empty)."""
        with self.lock:
            if not self.history:
                return None
            return min(self.history, key=lambda p: abs(p["t_unix_ms"] - target_ms))

    def newest(self):
        with self.lock:
            return self.history[-1] if self.history else None


# per-drone box colors (BGR): d0 green, d1 orange, d2 cyan, then repeat
DRONE_COLORS = [(0, 255, 0), (0, 160, 255), (255, 220, 0)]


def draw_overlay(tile, cam_block, scale_x, scale_y, latency_ms):
    for det in cam_block.get("detections", []):
        x1, y1, x2, y2 = det["bbox_xyxy"]
        p1 = (int(x1 * scale_x), int(y1 * scale_y))
        p2 = (int(x2 * scale_x), int(y2 * scale_y))
        # pad tiny boxes so they're visible on the shrunken tile
        if p2[0] - p1[0] < 6:
            cx = (p1[0] + p2[0]) // 2
            p1, p2 = (cx - 6, p1[1] - 4), (cx + 6, p2[1] + 4)
        color = DRONE_COLORS[det.get("drone_id", 0) % len(DRONE_COLORS)]
        if det.get("truncated"):
            color = tuple(int(c * 0.6) for c in color)
        cv2.rectangle(tile, p1, p2, color, 1)
        cv2.putText(tile, f"d{det['drone_id']} {det['dist_m']:.0f}m",
                    (p1[0], max(12, p1[1] - 4)), cv2.FONT_HERSHEY_SIMPLEX, 0.4, color, 1)
    cv2.putText(tile, f"{cam_block['name']}  lat {latency_ms:+.0f}ms",
                (6, TILE_H - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 0), 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--rtsp-port", type=int, default=8554)
    ap.add_argument("--label-port", type=int, default=9870)
    ap.add_argument("--cams", type=int, default=8)
    ap.add_argument("--delay-ms", type=float, default=300.0,
                    help="assumed video pipeline latency; labels are matched at (now - delay)")
    ap.add_argument("--save", default=None, metavar="PATH",
                    help="headless mode: write the grid image to PATH every 2s instead of showing a window")
    args = ap.parse_args()

    streams = [CameraStream(f"rtsp://{args.host}:{args.rtsp_port}/cam{i}", f"cam{i}")
               for i in range(args.cams)]
    for s in streams:
        s.start()
        time.sleep(0.4)  # stagger RTSP opens — 8 simultaneous probes overwhelm weak CPUs
    labels = LabelListener(args.label_port)
    labels.start()

    rows = (args.cams + GRID_COLS - 1) // GRID_COLS
    last_save = 0.0
    print(f"[viewer] {args.cams} streams from rtsp://{args.host}:{args.rtsp_port}/camN, "
          f"labels :{args.label_port}, mode={'save:' + args.save if args.save else 'window'}")

    while True:
        now_ms = time.time() * 1000.0
        packet = labels.at(now_ms - args.delay_ms)
        newest = labels.newest()
        lat = (now_ms - newest["t_unix_ms"]) if newest else 0.0
        cam_blocks = {c["name"]: c for c in packet["cameras"]} if packet else {}

        grid = np.zeros((rows * TILE_H, GRID_COLS * TILE_W, 3), dtype=np.uint8)
        for i, s in enumerate(streams):
            frame, ok = s.latest()
            tile = (cv2.resize(frame, (TILE_W, TILE_H)) if frame is not None
                    else np.zeros((TILE_H, TILE_W, 3), dtype=np.uint8))
            if frame is None or not ok:
                cv2.putText(tile, f"{s.name}: connecting...", (10, TILE_H // 2),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 1)
            block = cam_blocks.get(s.name)
            if block is not None and frame is not None:
                draw_overlay(tile, block, TILE_W / block["img_w"], TILE_H / block["img_h"], lat)
            r, c = divmod(i, GRID_COLS)
            grid[r * TILE_H:(r + 1) * TILE_H, c * TILE_W:(c + 1) * TILE_W] = tile

        header = f"frame {packet['frame_id']}" if packet else "no labels yet"
        if packet:
            d0 = packet["drones"][0]
            header += "  drone0 @ ({:.0f},{:.0f},{:.0f})  vis in {}/{} cams".format(
                *d0["pos_w"], sum(1 for b in cam_blocks.values() if b["detections"]), args.cams)
        cv2.putText(grid, header, (8, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1)

        if args.save:
            if time.time() - last_save > 2.0:
                cv2.imwrite(args.save, grid)
                last_save = time.time()
                print(f"[viewer] {header}")
            time.sleep(0.05)
        else:
            cv2.imshow("DroneDetection sim — ground truth overlay", grid)
            if cv2.waitKey(30) & 0xFF == 27:  # ESC
                break


if __name__ == "__main__":
    main()
