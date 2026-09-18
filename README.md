# Cape Crown — trainer-driven Camps Bay ride

**Current Unity scene:** `Assets/StoryCycling/Scenes/CampsBayTrainerRide.unity`
**Open the saved scene directly.** KICKR Core drives the rider; touch is only for menus, pairing, pause and audio.

Includes a larger coastal frontage, warm evening lighting, start menu, live km/h / watt / heart-rate HUD, native iOS BLE telemetry plugin, and original ambient music. See [setup and verified scope](docs/camps-bay-trainer-ride.md).

**Not yet hardware-verified:** iPad BLE, signed Xcode build, device FPS. Click shifting and hill resistance remain open in the Unity app.

---

## Earlier prototype notes (historical)

# Story Cycling — Cape Crown

iPad story-cycling project: winter training in South Africa, a fictional five-race
career and trainer-powered rides. This repository currently contains two clients.

## Current Unity branch: `unity-world`

Open the repository root in **Unity 6000.6.2f1**. Open the saved scene:

`Assets/StoryCycling/Scenes/CampsBayHillRide.unity`

A coastal promenade, an 8 m inland climb/lookout, animated prototype cyclist,
and touch/keyboard demo controls. Pulling includes the scene and generated art;
no builder invocation is needed just to run it.

- Play → **25 km/h DEMO** for a lap
- Hold **TRETEN** or W/up to accelerate; **BREMSEN** or S/down to brake
- **PAUSE / WEITER** and safe-area touch controls for the iPad visual beta
- Optional rebuild: **Story Cycling → Build Camps Bay Hill Ride**
- iPad export: **Story Cycling → iPad → Export Xcode Project**

See [iPad build and testing guide](docs/ipad-visual-beta.md).
The existing flat loop/promenade builders remain as comparisons.

**Important:** Unity trainer integration is not implemented yet. The hill does
not command KICKR resistance. Touch speed is explicitly labelled as demo data.
The Unity app uses its own bundle ID and does not overwrite the native BLE app.

## Native Bluetooth prototype

`StoryCycling.xcodeproj` is the separate Swift iPad prototype, not the Unity
scene. Use it for the existing trainer/heart-rate work. Click shifting and
resistance behaviour require their own hardware verification.

## Quality gates

- `bash scripts/ci/unity-world-preflight.sh`: paths, assets and source metadata
- Unity scene builder: route joins, geometry, ground/road clearance and rig refs
- **Story Cycling → QA → Review Hill Ride**: a complete Play Mode lap, touch
  handlers and camera captures under `Logs/HillReview`
- iOS export report: `Logs/ipad-export.txt`

GitHub's Unity preflight alone is **not** a Unity compile, render or device test.
Actual iPad installation, FPS/thermal profiling and KICKR testing remain
separate acceptance gates. `Library`, logs, Xcode exports and caches stay out of Git.
