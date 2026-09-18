# Camps Bay trainer experience

## Product contract
This is a KICKR-controlled training app, not a touch cycling game. Touch is for start, pause, device pairing and audio settings only. No keyboard, cruise or touch propulsion is compiled into this scene. The editor QA fixture is excluded from iPad builds and cannot earn training distance.

## Open / build
Open `Assets/StoryCycling/Scenes/CampsBayTrainerRide.unity` (saved scene).
Regeneration: **Story Cycling > Build Camps Bay Trainer Ride**.
iPad: **Story Cycling > iPad > Prepare Trainer Ride**, then **Export Xcode Project**.

## Flow
1. Start menu shows the coast, route length and climb.
2. Devices: search and select a KICKR Core / FTMS trainer; optional BLE heart-rate strap.
3. Start becomes available only after notification subscription AND a valid speed sample. Zero speed is a valid sample; missing data is not zero.
4. HUD displays speed, power and heart rate. Each metric has an independent freshness timeout. Missing values show `—`.
5. Speed drives the rider. Cadence drives pedalling where available. Stale speed stops and pauses the ride; reconnect, receive fresh data, and explicitly resume.
6. Settings pause an active ride. Audio mute/volume persist locally. Original 64-second music loop fades in and pauses in background.

## Visual direction
Camps Bay-inspired, compact coastal loop, not an accurate geographic model. Larger 7–10 m two/three-storey facades with balconies, aligned cafe terraces, warm evening sky, palms and Atlantic beach. Reference: https://www.capetown.travel/neighbourhood/camps-bay/
No further asset purchase required.

## Hardware scope / honest limitations
- Unity native CoreBluetooth plugin implements discovery, selection, connection, notification subscription, read-only FTMS Indoor Bike Data and Heart Rate Service. It does not use Unity Asset Store BLE plugins.
- Zwift Click and trainer resistance/hill simulation are **not implemented by this Unity plugin yet**. The settings screen labels Click accordingly. Do not infer resistance behavior from the visual grade HUD.
- Native syntax checks against iPhoneOS SDK in CI and Unity iOS export are NOT an Xcode device build or physical hardware test.
- Required next device acceptance: KICKR speed/power/cadence, HR, stop pedalling, disable Bluetooth, reconnect/resume, ten-minute render performance. No claims of measured device FPS until tested.
- The previous native Swift prototype is retained, not silently substituted for the Unity app.

## QA
**Story Cycling > QA > Review Trainer Experience** runs an explicitly labeled editor-only feed through start/menu/stale-data/pause checks and a complete loop. Rendered checkpoints and gate output are in ignored `Logs/ExperienceReview/`. C# parsing tests reject truncated/split packet mistakes and contactless HR values. No campaign storage is currently connected; `TrainingMetres` excludes the QA feed.

## Audio provenance
`Assets/StoryCycling/Resources/CampsBayEvening.wav`: original deterministic score and synthesis generated for this project by `scripts/audio/render-coastal-loop.py`. No third-party recordings, samples, or recognizable song melody. Source and composition belong to the project; this is a prototype soundtrack, not licensed commercial music.
