# macOS (Apple Silicon) deployment runbook

Target: team Mac (M-series, 32 GB). Much simpler than the Linux-server path — it's a desktop
machine, so **no headless display tricks needed**. The app runs as a normal (small) window on
the logged-in desktop and streams from there.

## 1. Prerequisites (once)

```bash
# Homebrew if missing: https://brew.sh
brew install ffmpeg
ffmpeg -encoders | grep videotoolbox     # must list h264_videotoolbox (hardware encoder)

# MediaMTX (native binary, no Docker needed)
mkdir -p ~/mediamtx && cd ~/mediamtx
curl -L -o mtx.tar.gz https://github.com/bluenviron/mediamtx/releases/download/v1.19.0/mediamtx_v1.19.0_darwin_arm64.tar.gz
tar xzf mtx.tar.gz
```

## 2. Install the build

Download the prebuilt app from the team repo's releases:

```bash
cd ~
curl -L -o DroneSim-macOS.zip https://github.com/Tion-ping/system4-unity-simulation/releases/download/v0.2-hongkong/DroneSim-macOS.zip
unzip -q DroneSim-macOS.zip
```

Then de-quarantine it (unsigned build — macOS will block it otherwise):

```bash
xattr -dr com.apple.quarantine ~/DroneSim.app
chmod +x ~/DroneSim.app/Contents/MacOS/*
```

Runtime config: `DroneSim.app/Contents/Resources/Data/StreamingAssets/simconfig.json`.
Defaults are correct for everything-on-this-Mac. The sim auto-detects brew's ffmpeg at
`/opt/homebrew/bin/ffmpeg` even when launched from Finder (no PATH needed), and `auto`
encoder mode picks **videotoolbox** on macOS (x264 fallback automatic).

## 3. Run

```bash
cd ~/mediamtx && ./mediamtx &                 # terminal 1 (or a Login Item)
open ~/DroneSim.app                           # the sim — small window appears
```

Verify:

```bash
ffprobe -rtsp_transport tcp rtsp://127.0.0.1:8554/cam0     # h264 1280x720
python3 Tools/check_labels.py                               # one ground-truth datagram
```

Keep-alive niceties:
- Prevent machine sleep while simming: run the app via `caffeinate -dis open ~/DroneSim.app`
  (or System Settings > Energy: prevent sleep).
- App Nap should not engage (the app renders continuously); if streams ever stall when the
  window is fully hidden, keep the window visible on a corner of the desktop.

## 4. ML model on the same Mac

**Important: no CUDA on Apple Silicon.**
- Best: run the model natively with PyTorch **MPS** backend (Apple GPU) or CoreML.
- Docker works for CPU-only inference (arm64 images; amd64 runs under slow Rosetta).
  From a container, reach the streams at `rtsp://host.docker.internal:8554/camN`
  (NOT localhost — Docker on macOS has no true host networking), and publish `-p 9870:9870/udp`
  to receive labels (sim sends to `127.0.0.1:9870` by default).
- Native processes simply use `rtsp://127.0.0.1:8554/camN` and bind UDP `:9870`.

## 5. Expected performance

8x720p@15 rendering + VideoToolbox encode is a light load on an M-series GPU/Media Engine;
the machine stays mostly free for inference. If the ML team wants more pixels, bump
`capture.width/height` in simconfig.json — the M5 has headroom for 8x1080p comfortably.

## Ports (same as everywhere)

| Port | Proto | What |
|---|---|---|
| 8554 | TCP | RTSP (MediaMTX) |
| 8888 | TCP | HLS browser preview |
| 9870 | UDP | Ground-truth labels (sim -> ML) |
| 9871 | UDP | RC control, Phase 2 |
