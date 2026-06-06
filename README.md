# DroneDetection Simulator

Unity 6 simulation that flies a drone through a real-scale Hong Kong (Victoria Harbour) and
streams **8 surveillance cameras as RTSP/H.264** plus **per-frame ground truth** (3D pose +
2D bboxes + camera intrinsics/extrinsics) over UDP — synthetic data for a drone-detection
ML pipeline.

## Quick start (consume the data)

| What | Where |
|---|---|
| Video | `rtsp://<host>:8554/cam0` … `cam7` (browser preview: `http://<host>:8888/cam0`) |
| Ground truth | UDP JSON on `:9870` — schema in [SIMULATION_PLAN.md](SIMULATION_PLAN.md) §4.2 |
| Reference viewer | `pip install -r Tools/requirements.txt && python Tools/viewer.py` |

## Run it

- **macOS (demo machine)**: [deploy/mac-setup.md](deploy/mac-setup.md) — prebuilt app in
  [Releases](https://github.com/Tion-ping/system4-unity-simulation/releases)
- **Windows**: build via `BuildScript.BuildWindows`, then `Tools/run_build_test.ps1`
- **Linux server (headless)**: [deploy/arch-setup.md](deploy/arch-setup.md)
- **Editor**: open `Assets/Scenes/HongKongScene.unity`, run MediaMTX, press Play
  (keep the Game tab visible)

## Scenes

- `HongKongScene` — real-scale Victoria Harbour; demo-tuned: tight drone loop ringed by
  8 cameras so it is detected nearly continuously. Tunables in `Assets/HongKong/hk_setup.json`
  (apply via the `DroneSim/HK/*` menu items).
- `SimScene` — simple block city (easy-mode data / fallback).

Both speak the identical protocol. Runtime knobs (resolution, fps, encoder, drone scale):
`Assets/StreamingAssets/simconfig.json` (in builds: `*_Data/StreamingAssets/`).

Full architecture, protocols, ports, and milestones: [SIMULATION_PLAN.md](SIMULATION_PLAN.md).
