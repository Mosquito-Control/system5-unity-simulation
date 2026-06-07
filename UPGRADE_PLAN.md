# Upgrade Plan — Demo Polish Round (Sat, Jun 7)

Three asks: multiple drones, realistic cameras (12, clustered), better close-up visuals
(hero buildings near + HK model far). Decisions locked with Marius Jun 6 late evening:
**priority = visuals first**, golden dusk, autonomous CC0 asset hunt, compact demo zone
(coverage area stays small on purpose).

Execution order: **V1 dusk → V2 cameras → V3 hero set → V4 multi-drone → rebuild/release.**
Each step leaves the demo in a working state; any step can be the stopping point.

---

## V1 — Golden dusk + emissive windows (~2h, biggest look-per-hour)

- Sun at 6–10° elevation, ~2800K warm, over the harbor axis; warmer fog (the haze stays
  as the scenic "cut"); sky atmosphere thickened for the orange horizon band.
- **Emissive windows without artist time:** generate window masks from the existing
  `Wall1..6_Color.png` via Python (threshold bright/glassy pixels with cv2 — tooling we
  already have) → `Wall*_Emission.png` → URP Lit emission slot, warm tint, bloom catches it.
  Crude masks read as "lit offices" from 100m+, which is all we need.
- Water material: darker base + high smoothness → sunset reflections (covers the
  shader-polish ask for the biggest surface in frame).
- All knobs go into `hk_setup.json` (`look.emission`, sun preset) — re-runnable via
  `DroneSim/HK/6 Apply Look`.
- **Acceptance:** dusk postcard + camera frames show lit windows; label sampling still
  ≥9/10 with detections (golden dusk keeps the drone learnable — that's why not night).

## V2 — Twelve realistically mounted cameras (~3h)

- `simconfig.json` grows to cam0..cam11 (capture manager + viewer already handle N;
  viewer grid becomes 3×4 automatically).
- **CCTV prop prefab** (primitives): galvanized pole (4–6m) + arm + camera housing, and a
  wall-bracket variant. Every camera gets a visible prop — cameras appear in each other's
  frames like a real installation. Props on the Default layer (never occlude their own view).
- Mount plan around the compact zone (all within ~600m of loop center, tight cluster as
  requested): 4 promenade poles (4–6m) along the waterfront, 3 wall-mounted on hero
  buildings (15–40m), 3 rooftop-edge mounts (60–120m), 2 high wide-angle on a tower corner
  (replaces the floating 550m eye — grounded, but still the overview shot). FOV mix 40–60°.
- Builder change: `Apply Cameras` learns to *create* missing camN + prop (today it only moves
  existing ones).
- **Platform note:** 12 H.264 sessions is fine on the M5 (VideoToolbox has no session cap)
  and fine via x264; if the NVIDIA/Linux path ever returns, consumer NVENC caps at 8 →
  per-camera encoder override goes on the backlog.
- **Acceptance:** 12/12 streams probe OK; per-camera screenshot audit (no interiors, no
  floaters); detection sampling ≥9/10 samples with ≥2 cameras detecting.

## V3 — Hero waterfront set (~half day, the risky one)

- **Hunt (autonomous):** CC0/CC-BY only, direct downloads (Poly Pizza, Kenney, itch.io,
  OpenGameArt, ambientCG/Poly Haven for PBR textures). Everything recorded in
  `deploy/ATTRIBUTIONS.md`. Repo-safe licenses only.
- **Quality gate at +2h:** if found meshes look worse than what we can build, switch to the
  guaranteed fallback: **10–15 hero towers built from clean proportioned boxes + setbacks,
  skinned with high-res CC0 PBR facade/glass textures (ambientCG) + our emission masks.**
  At dusk with bloom this reliably beats mediocre free meshes.
- **Placement:** along the promenade strip on the route's near side (roughly x 400–1400,
  z 1350–1550) — the zone 9 of 12 cameras look across or out from. No surgery on the merged
  HK meshes (impossible anyway): heroes stand in front, fog + dusk blend the transition to
  the low-res backdrop. Colliders + `Buildings` layer so occlusion labels stay honest.
- Budget: ≤500k added triangles, ≤50MB textures (git-LFS handles it).
- **Acceptance:** near/mid-ground of promenade cams dominated by hero buildings; clearance
  re-check 0 hits; M5-class perf unaffected (dev laptop slideshow is tolerated and expected).

## V4 — Multi-drone: 3 on separate loops (~2h)

- Three loops in altitude bands over the same zone: low 60–120m, mid 120–200m, high 180–260m,
  phase-spread so cameras usually see ≥2 drones.
- Per-drone visual variety for free: body tint per id (dark grey / graphite-orange / light) via
  MaterialPropertyBlock — also helps ML generalization.
- Plumbing: `hk_setup.json` paths[] per drone; builder spawns Drone_1/2 + Path_1/2 and
  clearance-checks each; `DronePathFollower` gets a start-offset param.
- Schema/Tools: `LabelPublisher` already emits drones[] + per-detection `drone_id` (designed
  for this day one). Viewer: per-id box colors (~5 lines).
- **Acceptance:** labels show 3 drones; multiple `drone_id`s in one packet; viewer shows
  distinct colored boxes; per-path clearance 0.

## Ship (~1h)

Rebuild macOS + Windows → smoke test (streams + detection sampling) → push →
release `v0.3-demo` with new `DroneSim-macOS.zip` → friend re-downloads.

## Explicitly parked

- **S1 RC control** — only if Sunday morning is free after this lands.
- Night mode, distractor objects (birds), recorder mode, per-camera encoder override.

## Risk table

| Risk | Mitigation |
|---|---|
| Free meshes look bad | 2h quality gate → textured-box heroes (guaranteed look, fully in our control) |
| 12 × x264 saturates dev laptop | Verification via probes/labels, not smoothness; local tests can disable 4 cams in config; M5 is the demo truth |
| Emission masks catch non-window pixels | Threshold iteration per wall texture; worst case lower emission intensity |
| Dusk dims the drone for ML | Golden (not blue) hour keeps sky/water bright behind the drone; sampling acceptance gates it |
| Time overrun | Order is strictly value-sorted; every step ends demo-ready |
