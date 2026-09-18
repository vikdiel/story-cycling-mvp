> Superseded: use [the trainer ride](camps-bay-trainer-ride.md). Touch acceleration/cruise below described an internal prototype and is not part of the product.

# Camps Bay Hill Ride — iPad visual beta

This is the Unity visual/controls beta, separate from the native Swift Bluetooth
prototype. It does **not** yet connect KICKR, heart-rate straps or Zwift Click.
Slope shown here is world geometry, not a resistance command to the trainer.

## Scene

Open `Assets/StoryCycling/Scenes/CampsBayHillRide.unity` directly. Its generated
meshes/materials are committed, so pulling no longer requires rebuilding first.
For deliberate regeneration: **Story Cycling → Build Camps Bay Hill Ride**.
The accepted flat `CapeCrownLoop` and `CampsBayPromenade` remain available.

The fictional loop has an 8 m inland summit and a smooth maximum 5.72% grade.
It is Camps-Bay-inspired, not an accurate map of Victoria Road. The physical
left-lane distance is shorter than the nominal centreline; wheel speed follows
3D distance including the climb. The HUD shows route height and signed grade.
No physical slope/resistance simulation is asserted in the touch demo.

## Controls

- Hold **TRETEN** to accelerate, release to coast
- Hold **BREMSEN** to brake and cancel cruise; sliding off releases the button
- **25 km/h DEMO** toggles cruise
- **PAUSE / WEITER** stops/resumes the session; resume does not auto-accelerate
- Backgrounding/losing focus pauses and releases all held inputs
- Keyboard W/up, S/down and Space retain their previous roles

Buttons and telemetry respect the iPad safe area. Demo speed is labelled as
simulation, not live trainer data. The geometric cyclist is still a prototype.

## One-time prerequisites on the build Mac

Unity **6000.6.2f1**, **iOS Build Support**, Xcode and an Apple signing team.
The Mini can generate an Xcode project; signing, installing and measuring iPad
FPS require Xcode and the physical iPad on the deployment Mac.

## Export and install

1. **Story Cycling → iPad → Prepare Visual Beta**
2. **Story Cycling → iPad → Export Xcode Project**
3. Open the timestamped `Builds/iPad/CapeCrown-…/Unity-iPhone.xcodeproj` in Xcode
4. Choose the iPad and your team under Signing & Capabilities, then Run

Bundle ID: `com.vikdiel.capecrown.unitybeta` (does not replace the native BLE app).
Exports never overwrite an earlier export. Signing team IDs are not hard-coded.
Builds are ignored by Git. On the MacBook, pull and export there; no need to
transfer the large generated Xcode directory from the Mini.

Configuration: iPad-only, landscape in both directions, Metal, ARM64, IL2CPP,
low managed stripping, development build. Mobile URP uses 0.85 render scale,
2x MSAA, HDR off, one shadow cascade and 45 m shadow distance. Runtime targets
30 FPS; **target is not a measured device result**.

## Verification

- Dependency preflight: `bash scripts/ci/unity-world-preflight.sh`
- Geometry: closed mesh, tangent joins, 10 m road width, <=6% grade, physical speed
- Scene builder: footprint clearance, no shop overlaps, rig/save/reopen checks
- **Story Cycling → QA → Review Hill Ride**: real Play Mode lap, pointer-handler
  controls, five camera captures and brake/pause checks in `Logs/HillReview`
- Export report in `Logs/ipad-export.txt`; this is not an Xcode signing report
- Device acceptance still requires a complete iPad lap, touch safe-area check,
  app background/resume, FPS/thermal check and memory profiling

Unity reference: [iOS environment setup](https://docs.unity.com/en-us/engine/6000.6/manual/platform-specific/iphone/getting-started/ios-environment-setup)
