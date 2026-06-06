#!/usr/bin/env python3
"""Sanity check: receive one ground-truth label datagram and print a summary.
Usage: python3 check_labels.py [port]   (default 9870; run on the machine the sim sends to)
"""
import json
import socket
import sys

port = int(sys.argv[1]) if len(sys.argv) > 1 else 9870
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", port))
s.settimeout(10)
try:
    data = s.recvfrom(65535)[0]
except socket.timeout:
    print(f"TIMEOUT: no labels on :{port} within 10s")
    sys.exit(1)

p = json.loads(data.decode("utf-8"))
dets = {c["name"]: len(c["detections"]) for c in p["cameras"]}
print(f"OK {len(data)} bytes | frame {p['frame_id']} | drone0 @ {[round(v,1) for v in p['drones'][0]['pos_w']]}"
      f" | detections per cam: {dets}")
