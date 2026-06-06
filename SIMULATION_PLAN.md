# Drone Detection — Synthetic Data Simulation Plan

Unity 6 (6000.4.10f1, URP) simulation that renders a city with a flying drone, captures 8 virtual
surveillance cameras, streams them as RTSP/H.264 to an ML drone-detection model, publishes
ground-truth labels over UDP, and deploys as a headless graphical Linux app on a GPU Arch server.

**Deadline: working end-to-end Sunday, Jun 8 2026.** RC control is the stretch goal (Phase 2).

---

## 1. Locked decisions

| Topic | Decision |
|---|---|
| Video transport | RTSP / H.264 — Unity pipes raw frames into per-camera `ffmpeg` processes, which publish to a MediaMTX server |
| ML model location | Same machine (Arch server), separate Docker container |
| Ground truth | 2D bboxes per camera **and** 3D drone pose + full camera intrinsics/extrinsics, via UDP JSON |
| Recording mode | None — live streaming only |
| Cameras (default) | 8 × 1280×720 @ 15 fps, all parameters config-driven |
| Camera placement | Mixed: 4 perimeter poles (~6 m) + 4 rooftops, all covering the flight volume |
| Drone visual | Quadcopter built from primitives, as a prefab so a realistic model can be swapped in later |
| Flight model | Phase 1: kinematic Catmull-Rom spline over waypoints. Phase 2: Rigidbody + PID, RC-controlled |
| Drone count | 1 active; spawner + label schema support N |
| RC input (Phase 2) | UDP bridge: radio → USB HID joystick on any laptop → Python script → UDP to sim |
| Server GPU | NVIDIA → headless X (`AllowEmptyInitialConfiguration`) + NVENC encoding |
| Deployment | Sim bare-metal under systemd + virtual X display; MediaMTX and ML model in Docker |
| City | Hand-placed gray blocks. Functional only, no visual polish |
| Verification | Python reference viewer: 8-stream grid + label overlay + latency readout |

---

## 2. Architecture

```
┌─────────────────────────── Arch Linux server (NVIDIA GPU) ───────────────────────────┐
│                                                                                       │
│  Xorg :1 (virtual display, no monitor — nvidia AllowEmptyInitialConfiguration)        │
│  └── DroneSim.x86_64  (Unity Linux player, systemd service, DISPLAY=:1)              │
│       │                                                                               │
│       │  scene: block city + drone on spline + 8 disabled-render surveillance cams    │
│       │                                                                               │
│       ├─ CaptureManager ── per cam: RenderTexture → AsyncGPUReadback ──┐              │
│       │                                                                ▼              │
│       │                       8 × ffmpeg child processes (rawvideo on stdin,          │
│       │                          h264_nvenc, RTSP publish) ──────────┐                │
│       │                                                              ▼                │
│       │                                       ┌────────────────────────────┐          │
│       ├─ LabelPublisher ── UDP JSON :9870 ──▶ │  Docker                    │          │
│       │                                       │  ├─ mediamtx  (:8554 RTSP) │          │
│       └─ RcUdpReceiver  ◀─ UDP JSON :9871     │  └─ ML model container     │          │
│             (Phase 2)                         │      cv2.VideoCapture(     │          │
│                                               │       rtsp://...:8554/camN)│          │
│                                               └────────────────────────────┘          │
└───────────────────────────────────────────────────────────────────────────────────────┘
                ▲ UDP :9871 (Phase 2)                      ▲ rtsp://server:8554/camN
                │                                          │ udp labels (optional remote)
   ┌────────────┴────────────┐                ┌────────────┴────────────┐
   │ Colleague's laptop      │                │ Dev / verification      │
   │ FPV radio (USB HID)     │                │ Tools/viewer.py         │
   │ Tools/rc_bridge.py      │                │ 8-grid + bbox overlay   │
   └─────────────────────────┘                └─────────────────────────┘
```

Data flow per captured tick (15 Hz): all 8 cameras render the **same** sim frame → one global
`frame_id` → 8 raw frames go to the encoders, one labels datagram (all cameras, all drones,
same `frame_id`) goes out over UDP.

---

## 3. Unity scene & components

### 3.1 Scene hierarchy (`Assets/Scenes/SimScene.unity`)

```
SimScene
├── Sim                        ← SimBootstrap, SimConfig, CaptureManager, LabelPublisher
├── Environment
│   ├── Ground                 (500×500 m plane, gray)
│   └── Buildings              (~30 hand-placed boxes; static)
├── SurveillanceRig
│   ├── cam0 … cam3            (perimeter poles, ~6 m)
│   └── cam4 … cam7            (rooftops, 35–55 m)
├── Drones
│   └── Drone_0                (prefab instance)
├── FlightPaths
│   └── Path_0                 (DronePathFollower target; waypoints as children)
├── Directional Light          (fixed midday sun, soft shadows ON — shadow is a real ML cue)
└── SpectatorCamera            (dev-only free camera; renders the tiny app window)
```

### 3.2 City spec (functional only)

- Ground: 500×500 m plane at y=0, mid-gray material.
- ~30 box buildings: footprints 15–40 m, heights 10–60 m (taller cluster near center),
  street gaps 15–25 m, 2–3 gray shades so facades are distinguishable.
- All static, plain `BoxCollider`s (needed for occlusion raycasts + Phase 2 crash detection).
- Layer `Buildings` (used by the occlusion mask).
- Placed once via editor scripting, then they're ordinary GameObjects — drag to taste.

### 3.3 Camera rig

Each `camN` is a Unity Camera, **disabled for normal rendering** (no display target), with a
private 1280×720 RenderTexture. FOV 60° vertical, near 0.3, far 1000.

| Cam | Mount | Position (approx) | Aim |
|---|---|---|---|
| cam0 | pole NW | (−160, 6, 160) | flight volume center (0, 35, 0) |
| cam1 | pole NE | (160, 6, 160) | (0, 35, 0) |
| cam2 | pole SE | (160, 6, −160) | (0, 35, 0) |
| cam3 | pole SW | (−160, 6, −160) | (0, 35, 0) |
| cam4 | roof N | (0, ~45, 90) | across center, slightly down |
| cam5 | roof E | (90, ~50, 0) | across center |
| cam6 | roof S | (0, ~40, −90) | across center |
| cam7 | roof W | (−90, ~55, 0) | across center |

Rooftop positions snap to actual hand-placed buildings at placement time. Result: the flight
volume is always seen by ≥4 cameras, from low-up, high-down and lateral angles.

### 3.4 Drone prefab (`Assets/Prefabs/Drone.prefab`)

```
Drone (root: DronePathFollower, DroneVisual, later DronePhysicsController)
└── Visual                     ← swap point: replace this child with a real model later
    ├── Body                   (dark gray box ~0.35×0.12×0.35)
    ├── Arm_FL/FR/BL/BR        (thin boxes, X-frame)
    ├── Rotor_FL/FR/BL/BR      (dark cylinders Ø0.18, DroneVisual spins them)
    └── CameraPod              (small front box — gives an orientation cue)
```

- Default scale factor **2.0** (≈0.9 m wheelbase) via config — see §8 pixel-size math for why.
- `DroneVisual`: spins rotor discs (~3000°/s, alternating direction).
- Labels use the combined renderer bounds of `Visual`, so a swapped model auto-works.

### 3.5 Flight path (Phase 1)

`DronePathFollower` (kinematic):
- Waypoints = child transforms of `Path_0`, closed Catmull-Rom spline.
- Constant speed (config, default 8 m/s), loops forever.
- Faked attitude: pitch/roll proportional to acceleration (cap ±25°), yaw follows velocity.
- Default path: ~12 waypoints weaving between buildings, altitude 10–80 m, distance to nearest
  camera mostly 20–120 m (scale diversity in every camera).

### 3.6 Scripts (`Assets/Scripts/Sim/`)

| Script | Responsibility |
|---|---|
| `SimBootstrap.cs` | Entry point: loads config, `targetFrameRate = fps`, `vSyncCount = 0`, owns global `frame_id`, drives the capture tick, clean shutdown of child processes |
| `SimConfig.cs` | Parses `StreamingAssets/simconfig.json` (schema §6); plain serializable classes |
| `CaptureManager.cs` | Per enabled camera: render into RT each tick, `AsyncGPUReadback` (RGBA32), hand completed frames + `frame_id` to its `FfmpegPipe` |
| `FfmpegPipe.cs` | One ffmpeg child per camera: builds args, writer thread w/ bounded queue (depth 3, drop-oldest + log), restart with backoff on crash, hard-kill on app exit |
| `LabelPublisher.cs` | Each tick: project drone bounds into every camera, occlusion raycasts, build labels JSON, UDP send |
| `DronePathFollower.cs` | Spline follower (§3.5); implements `IDroneControlSource` so Phase 2 plugs in |
| `DroneVisual.cs` | Rotor spin |
| `FrameIdStamp.cs` | Optional (config flag): blits a 16-bit black/white strip with `frame_id` into the frame top-left, for exact video↔label alignment debugging. **Default OFF** (would pollute training data) |
| `RcUdpReceiver.cs` + `DronePhysicsController.cs` | Phase 2 (§9) |

### 3.7 Capture design decisions

- **Sim runs at capture rate (15 fps), cameras always-on.** Simplest correct option: one global
  tick, no wasted renders, perfect frame alignment across all 8 cams. Upgrade path for Phase 2
  (if RC needs a faster sim loop): sim at 50 Hz + `RenderPipeline.SubmitRenderRequest`
  (`StandardRequest`, URP-supported) to render cameras at 15 Hz only.
- **AsyncGPUReadback** (works on D3D11 + Vulkan): never stalls the GPU; `frame_id` is captured
  in the request closure so out-of-order completion can't mislabel frames.
- **Orientation/color:** Unity readback is bottom-left origin → ffmpeg applies `-vf vflip`.
  Project uses linear color space; RTs are sRGB so readback delivers gamma-encoded RGBA — correct
  for video. *Verify visually on both Windows/D3D11 and Linux/Vulkan early (known foot-gun).*
- **Backpressure:** if an encoder stalls, its queue drops oldest frames (stream skips, sim never
  blocks). Drops are logged with counts.

---

## 4. Streaming spec (what the ML team consumes)

### 4.1 Video

- URL per camera: `rtsp://<server>:8554/cam0` … `/cam7` (TCP transport recommended).
- H.264, 1280×720 @ 15 fps, ~4 Mbit/s, GOP 30 (2 s), no B-frames, low-latency tuned.
- Glass-to-glass latency expectation: ~150–400 ms.
- Bonus (free from MediaMTX): HLS at `http://<server>:8888/camN` for browser demo viewing.

ffmpeg invocation (per camera, NVENC) — **as implemented & verified**:

```
ffmpeg -hide_banner -loglevel warning \
  -f rawvideo -pix_fmt rgba -s 1280x720 -r 15 -i pipe:0 \
  -vf vflip,settb=AVTB,setpts=(RTCTIME-RTCSTART)/(TB*1000000) -an \
  -c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -g 30 -b:v 4M \
  -fps_mode passthrough \
  -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/cam0
```

CPU fallback (auto if NVENC unavailable, also per-camera via config):
`-c:v libx264 -preset ultrafast -tune zerolatency -bf 0 -g 30 -b:v 4M`

> **Why `setpts=RTCTIME` (learned the hard way):** frames are VFR-stamped with wallclock at
> arrival, so the stream stays real-time-correct even when the sim renders below target fps
> (verified: dev box at ~8 fps → stream pts span matched wall time; with plain `-r 15` CFR the
> video drifted 2× fast). `use_wallclock_as_timestamps` does NOT work — the rawvideo demuxer
> always generates index-based pts. On the server at a full 15 fps this degrades gracefully
> into effectively-CFR.

> **NVENC session limit:** consumer GeForce drivers allow 8 concurrent encode sessions
> (driver ≥ 550). We are exactly at 8. If anything else on the box uses NVENC, switch some
> cameras to `x264` in config — 720p15 ultrafast costs <1 core each.

### 4.2 Ground-truth labels — UDP JSON, port **9870**

One datagram per captured tick (covers all cameras + all drones, same `frame_id`).
~2–4 KB; loopback fragmentation is fine. If labels ever go cross-network, switch to
per-camera packets (config flag reserved).

```json
{
  "v": 1,
  "frame_id": 12345,
  "t_unix_ms": 1781234567890,
  "t_sim": 823.45,
  "drones": [
    { "id": 0,
      "pos_w": [12.3, 34.5, -8.1],
      "vel_w": [7.9, 0.1, -1.2],
      "rot_q": [0.0, 0.38, 0.0, 0.92] }
  ],
  "cameras": [
    { "name": "cam0",
      "img_w": 1280, "img_h": 720,
      "pos_w": [-160.0, 6.0, 160.0],
      "rot_q": [0.05, 0.92, -0.02, 0.38],
      "K": [623.5, 0, 640, 0, 623.5, 360, 0, 0, 1],
      "detections": [
        { "drone_id": 0,
          "bbox_xyxy": [612.2, 300.1, 671.8, 352.7],
          "center_px": [642.0, 326.4],
          "dist_m": 87.2,
          "visible": true,
          "truncated": false }
      ] }
  ]
}
```

Conventions (also enforced by the reference viewer):

- **World:** Unity convention — left-handed, +Y up, +Z forward, meters. Quaternions `[x,y,z,w]`.
- **Image:** origin **top-left**, u right, v down — i.e. pixel coords match the *delivered*
  (already-vflipped) video exactly.
- **K:** ideal pinhole, zero distortion. `fy = (img_h/2)/tan(fovY/2)`, `fx = fy`,
  `cx = img_w/2`, `cy = img_h/2`. (Tell the ML team: no lens distortion is simulated.)
- **bbox:** projection of the drone's renderer-bounds corners, clipped to the image.
  A detection is included only if: in frustum, bbox ≥ 2 px on both axes, and at least one of
  5 occlusion rays (center + 4 bound corners, mask `Buildings`) is clear. `truncated` = bbox
  was clipped at an image edge.
- **Alignment with video:** timestamp-based (`t_unix_ms` vs receive time minus measured pipeline
  offset) → exact to ±1 frame at 15 fps, fine for detection. For exact eval, enable the
  `FrameIdStamp` pixel strip and decode it client-side.

### 4.3 Docker networking (ML container ↔ sim)

MediaMTX runs with `network_mode: host` (Linux) — sim publishes to `rtsp://127.0.0.1:8554`,
ML container (host network too, simplest) reads the same URL. If the ML container must stay on
a bridge network: it reaches the host via `host-gateway` /
`extra_hosts: ["host.docker.internal:host-gateway"]` → `rtsp://host.docker.internal:8554/camN`.
Labels: sim sends UDP to a configurable `host:port` — for a bridge-network ML container,
publish `9870/udp` from that container and point the sim at `127.0.0.1:9870`.
**Test reachability with `ffprobe` from inside the ML container on day 1 of server work.**

---

## 5. Repo layout (target)

```
Assets/
  Scenes/SimScene.unity
  Prefabs/Drone.prefab
  Scripts/Sim/ (…scripts from §3.6)
  StreamingAssets/simconfig.json
Tools/
  viewer.py            ← reference client: 2×4 RTSP grid + bbox overlay + latency readout
  rc_bridge.py         ← Phase 2: HID joystick → UDP
  requirements.txt     (opencv-python, numpy; Phase 2: pygame)
deploy/
  mediamtx/docker-compose.yml
  systemd/sim-display.service    ← virtual X
  systemd/drone-sim.service      ← the sim itself
  arch-setup.md                  ← step-by-step server runbook (content: §7)
SIMULATION_PLAN.md     ← this file
```

---

## 6. Config — `Assets/StreamingAssets/simconfig.json`

Single source of runtime truth; edit + restart (no hot reload).

```json
{
  "capture": { "width": 1280, "height": 720, "fps": 15, "bitrate_kbps": 4000 },
  "encoder": { "mode": "auto", "ffmpeg_path": "ffmpeg" },
  "rtsp":    { "base_url": "rtsp://127.0.0.1:8554/" },
  "labels":  { "host": "127.0.0.1", "port": 9870, "embed_frame_id": false, "min_bbox_px": 2 },
  "cameras": [
    { "name": "cam0", "enabled": true },
    { "name": "cam1", "enabled": true },
    { "name": "cam2", "enabled": true },
    { "name": "cam3", "enabled": true },
    { "name": "cam4", "enabled": true },
    { "name": "cam5", "enabled": true },
    { "name": "cam6", "enabled": true },
    { "name": "cam7", "enabled": true }
  ],
  "drone":   { "scale": 2.0, "path_speed_mps": 8.0 },
  "control": { "udp_port": 9871, "failsafe_ms": 250, "mode": "autopilot" }
}
```

- `encoder.mode`: `auto` (probe NVENC, fall back to x264) | `nvenc` | `x264`.
- Camera *placement* is authored in the scene; config only toggles/parameterizes.
- `cameras[].enabled` is the perf escape hatch (drop to 4 cams with one edit).
- `control.mode`: `autopilot` | `rc` (Phase 2).

---

## 7. Linux build & headless Arch deployment

### 7.1 Build (on the Windows dev machine)

- Install **Linux Build Support (Mono)** module via Unity Hub. Scripting backend: **Mono**
  (fast to set up; `System.Diagnostics.Process` for ffmpeg works fine; IL2CPP unnecessary).
- Player settings: Linux x86_64, windowed 640×360 (the visible window is just the spectator
  cam; real output goes through the RTSP pipeline), Vulkan, no splash if license allows.
- Output: `Builds/linux/DroneSim.x86_64` + `_Data/` (config lives in
  `DroneSim_Data/StreamingAssets/simconfig.json` — editable on the server).

### 7.2 Server prerequisites (Arch)

```
sudo pacman -S nvidia nvidia-utils xorg-server xorg-xinit ffmpeg docker docker-compose
# ffmpeg from extra/ has NVENC; verify: ffmpeg -encoders | grep nvenc
```

### 7.3 Headless X — "the monitor trick"

NVIDIA renders happily on a real X server with no display attached:

```
sudo nvidia-xconfig --allow-empty-initial-configuration   # writes /etc/X11/xorg.conf
```

`deploy/systemd/sim-display.service`:
```ini
[Unit]
Description=Headless X for DroneSim
[Service]
ExecStart=/usr/bin/X :1 -nolisten tcp vt7
Restart=always
[Install]
WantedBy=multi-user.target
```

`deploy/systemd/drone-sim.service`:
```ini
[Unit]
Description=DroneSim
After=sim-display.service docker.service
Requires=sim-display.service
[Service]
Environment=DISPLAY=:1
Environment=__GL_SYNC_TO_VBLANK=0
ExecStart=/opt/dronesim/DroneSim.x86_64 -screen-width 640 -screen-height 360
Restart=on-failure
[Install]
WantedBy=multi-user.target
```

> **Do NOT use `-batchmode`** — for player builds it disables rendering entirely, which is the
> opposite of what we want. The whole point of the virtual X display is to run a *normal*
> rendering player without a monitor.

Quick manual test before systemd: `xinit /opt/dronesim/DroneSim.x86_64 -- :1`, then
`nvidia-smi` (sim listed, GPU busy) and `ffprobe rtsp://127.0.0.1:8554/cam0`.

### 7.4 MediaMTX — `deploy/mediamtx/docker-compose.yml`

```yaml
services:
  mediamtx:
    image: bluenviron/mediamtx:latest
    network_mode: host        # RTSP :8554, HLS :8888
    restart: unless-stopped
```

### 7.5 Ports

| Port | Proto | What |
|---|---|---|
| 8554 | TCP | RTSP (MediaMTX) |
| 8888 | TCP | HLS browser preview (bonus) |
| 9870 | UDP | Ground-truth labels (sim → ML) |
| 9871 | UDP | RC control (bridge → sim, Phase 2) |

---

## 8. Performance budget & pixel-size reality check

Budget @ 8×720p15 (RTX-class GPU): rendering a box city ≈ trivial; GPU→CPU readback
8 × 3.7 MB × 15 ≈ **440 MB/s** over PCIe (fine); NVENC ≈ free; total CPU ≈ 2–3 cores
(pipes + ffmpeg muxing). Leaves most of the GPU for ML inference.

**How big is the drone on screen?** (720p, FOV 60° → f ≈ 623 px)

| Distance | 0.45 m drone (real size) | 0.9 m (default, scale 2) |
|---|---|---|
| 30 m | ~9 px | ~19 px |
| 60 m | ~5 px | ~9 px |
| 100 m | ~3 px | ~6 px |
| 150 m | ~2 px | ~4 px |

Real surveillance is genuinely this hard — at default settings the drone is a handful of pixels
in far cameras. Knobs if ML needs more pixels (all config/scene-level, no code):
drone `scale`, path closer to cameras, narrower FOV, or 1080p capture. Flag this to the ML team
**before** they evaluate — it sets expectations on achievable detection range.

---

## 9. Phase 2 — RC control from the FPV transmitter

Architecture is already shaped for it: `DronePathFollower` and the RC stack both implement
`IDroneControlSource`; `control.mode` (or packet presence) selects which one drives the drone.

1. **`Tools/rc_bridge.py`** (colleague's laptop): EdgeTX/OpenTX radios enumerate as a standard
   USB HID joystick. Script reads axes via pygame, normalizes (configurable channel map +
   inversion), sends 50 Hz UDP JSON to the server:
   `{"v":1,"seq":n,"t_ms":...,"axes":{"roll":-1..1,"pitch":-1..1,"yaw":-1..1,"thr":0..1},"arm":true,"mode":"angle"}`
   Keyboard/Xbox-pad fallback in the same script → RC phase is testable without the radio.
2. **`RcUdpReceiver.cs`**: listens on :9871, validates `seq`, exposes latest command.
   **Failsafe:** >250 ms without packets, or `arm:false` → hover-hold, then auto-return to the
   spline autopilot after 3 s.
3. **`DronePhysicsController.cs`**: Rigidbody (m≈0.8 kg, drag tuned), **angle mode** default —
   sticks command tilt angle (±35°) via PD attitude controller, yaw rate, throttle = climb rate
   around hover thrust. Acro mode flag for the FPV pilot. Crash = collision with `Buildings`
   → respawn at path start, disarmed.
4. Streams and labels are untouched — physics drone feeds the exact same pipeline.

---

## 10. Milestones & schedule

| # | When | Deliverable | Acceptance test |
|---|---|---|---|
| **M0** | Fri eve | Scene: city blocks, drone prefab on spline path, 8 cameras placed | In editor: drone loops the path, visible in every camera preview |
| **M1** | Sat ~midday | Full pipeline on Windows dev box (ffmpeg + MediaMTX local) | `viewer.py` shows 8 live grids, bboxes track the drone, latency < 500 ms, 30 min without a dropped encoder |
| **M2** | Sat eve | Linux build on Arch server, headless X, services running | From ML container: `ffprobe` opens all 8 streams; `viewer.py` works against the server; `nvidia-smi` shows GPU rendering |
| **M3** | Sun | ML integration + hardening | Colleagues' model consumes streams + labels; sim survives hours; config tweaks (cam count/res) verified |
| **S1** | Sun (stretch) | RC: bridge + physics + failsafe | Colleague flies via radio from laptop; autopilot fallback on disconnect |

Build order within M0–M1: scene/city → drone+path → capture+ffmpeg (1 cam) → MediaMTX+viewer
(1 cam end-to-end) → scale to 8 → labels + overlay verification.

---

## 11. Risks & mitigations

| Risk | Mitigation |
|---|---|
| NVENC 8-session cap collides with other GPU users | `encoder.mode` per-camera; x264 ultrafast fallback is cheap at 720p15 |
| Image upside-down / wrong gamma on Vulkan vs D3D11 | Verified visually at M1 (Windows) **and** first thing at M2 (Linux); fixes isolated to ffmpeg `-vf` / RT sRGB flag |
| Docker networking blocks RTSP/UDP from ML container | host-network MediaMTX; `ffprobe`-from-container is the *first* M2 test |
| Unity player won't start on headless X | Standard NVIDIA recipe (§7.3); fallback: Xvfb + llvmpipe proves wiring (slow), then debug GPU X separately; `xinit` manual test before systemd |
| ffmpeg child processes leak/die | Supervisor in `FfmpegPipe`: restart with backoff, kill-on-exit, drop-oldest queues |
| Encoder back-pressure stalls sim | Bounded queues, never block main thread, drop + log |
| Sim fps below capture target (weak GPU) | VFR wallclock pts (§4.1) keep streams real-time-correct at any achieved fps — verified at ~8 fps on an Intel iGPU dev box |
| Mid-play domain reload kills pipes (editor only) | Editor pref set: Script Changes While Playing = Stop Playing And Recompile; never screenshot into Assets/ during play |
| Drone only a few px in far cams → ML finds nothing | §8 table shared with ML team up front; scale/path/FOV/res knobs in config |
| Label↔frame misalignment | Same-tick capture for all cams, `t_unix_ms` matching (±1 frame), optional `FrameIdStamp` for exact alignment |
| Weekend deadline | RC is explicitly stretch; camera count is the perf dial; every milestone ends in a demoable state |

## 12. Explicitly out of scope (this weekend)

Domain randomization (lighting/weather/textures), multiple active drones (schema ready),
distractor objects (birds/balloons), lens distortion & sensor noise, dataset recorder,
nicer drone mesh (prefab swap point ready), city visual polish.

---

## 13. Phase 1 implementation checklist

- [ ] City: ground + ~30 blocks, `Buildings` layer + colliders
- [ ] Drone prefab (primitives, rotor spin, `Visual` swap point)
- [ ] `Path_0` waypoints + `DronePathFollower` (spline, faked banking)
- [ ] 8 cameras placed per §3.3, RTs assigned
- [ ] `SimConfig` + `simconfig.json`
- [ ] `CaptureManager` + `FfmpegPipe` (1 cam → 8 cams)
- [ ] MediaMTX up (Docker on dev box or server), streams visible in `ffplay`
- [ ] `LabelPublisher` (projection, occlusion rays, UDP)
- [ ] `Tools/viewer.py` (grid + overlay + latency)
- [ ] Linux build, deploy per §7, M2 acceptance tests
- [ ] ML container integration test (M3)
