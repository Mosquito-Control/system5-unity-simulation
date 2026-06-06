# Arch server deployment runbook

Goal: the Unity sim renders GPU-accelerated **without a monitor** on a headless X display,
publishes 8 RTSP streams into MediaMTX (Docker), labels via UDP. The ML container reads both.

## 1. Prerequisites (once)

```bash
sudo pacman -S nvidia nvidia-utils xorg-server xorg-xinit ffmpeg docker docker-compose
ffmpeg -encoders | grep nvenc          # must list h264_nvenc
sudo systemctl enable --now docker
```

## 2. Headless X config (once)

```bash
sudo nvidia-xconfig --allow-empty-initial-configuration   # writes /etc/X11/xorg.conf
```

If the box has multiple GPUs, pin the right one: `nvidia-xconfig --busid=PCI:<bus>` (get the
bus id from `nvidia-smi --query-gpu=pci.bus_id --format=csv`).

## 3. Install the build

Copy the Linux build (from Unity: Linux x86_64, Mono, Vulkan) to the server:

```bash
sudo mkdir -p /opt/dronesim
# from the dev machine:
#   scp -r Builds/linux/* user@server:/tmp/dronesim && ssh user@server 'sudo mv /tmp/dronesim/* /opt/dronesim/'
sudo chmod +x /opt/dronesim/DroneSim.x86_64
```

Runtime config lives at `/opt/dronesim/DroneSim_Data/StreamingAssets/simconfig.json`
(camera count/res/fps, encoder mode, label target). Edit + restart the service to apply.

## 4. MediaMTX

```bash
cd deploy/mediamtx && docker compose up -d
```

## 5. Manual smoke test (before systemd)

```bash
xinit /opt/dronesim/DroneSim.x86_64 -- :1 &
sleep 10
nvidia-smi                                          # sim listed, GPU busy, ffmpeg sessions
ffprobe -rtsp_transport tcp rtsp://127.0.0.1:8554/cam0   # h264 1280x720
```

## 6. Services

```bash
sudo cp deploy/systemd/*.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now sim-display drone-sim
journalctl -fu drone-sim       # watch sim stdout (Unity Debug.Log)
```

## 7. ML container access

- Simplest: run the ML container with `--network host` → streams at `rtsp://127.0.0.1:8554/camN`,
  labels by binding UDP `:9870`.
- Bridge network instead: add `extra_hosts: ["host.docker.internal:host-gateway"]` and read
  `rtsp://host.docker.internal:8554/camN`; publish `9870/udp` from the ML container and set
  `labels.host=127.0.0.1` in simconfig.json (the sim sends into the published port).
- **First test from inside the ML container:** `ffprobe rtsp://…/cam0` — do this before any model work.

## 8. Ports

| Port | Proto | What |
|---|---|---|
| 8554 | TCP | RTSP (MediaMTX) |
| 8888 | TCP | HLS browser preview |
| 9870 | UDP | Ground-truth labels (sim → ML) |
| 9871 | UDP | RC control, Phase 2 (bridge → sim) |

## Troubleshooting

| Symptom | Fix |
|---|---|
| `X :1` exits immediately | check `/var/log/Xorg.1.log`; usually missing `--allow-empty-initial-configuration` or wrong BusID |
| sim runs but streams down | `journalctl -u drone-sim | grep '\[Sim\]'` — ffmpeg start errors appear here; check `ffmpeg` is in PATH for the service (absolute path via `encoder.ffmpeg_path` if needed) |
| nvenc errors with >N sessions | consumer driver caps concurrent NVENC sessions (8 on recent drivers); set `"mode":"x264"` for some cameras in simconfig.json |
| streams stutter / latency grows | check sim achieved fps in journal; frames are VFR wallclock-stamped so timing stays correct, but if fps is far below target reduce enabled cameras or resolution |
| black/upside-down video | should not happen (vflip handled); if colors look washed out on Vulkan, report — sRGB readback issue |
