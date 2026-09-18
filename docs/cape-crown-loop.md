# Cape Crown loop — rebuild

For the new beach art/animation pass see [Camps Bay promenade](camps-bay-promenade.md).
The user confirmed one complete lap, acceleration and braking on the baseline
loop on 2026-09-18. This is not visual acceptance of the new promenade scene.

1. On `unity-world`, pull changes. Stop Unity Play Mode.
2. **Story Cycling → Build Cape Crown Loop**. This saves and opens
   `Assets/StoryCycling/Scenes/CapeCrownLoop.unity` and sets it as the build scene.
   The old Vertical Slice menu is an alias; old saved scenes are not modified.
3. Play, focus Game. **W/↑** accelerates to 50 km/h, **S/↓** brakes.
   **Space** toggles 25 km/h demo cruise. This is explicitly keyboard simulation,
   not a working Unity Bluetooth bridge. `SetTrainerSpeed` disables simulation.
4. The editor builder executes geometry assertions: closure, tangent continuity,
   10 m road width, rider clearance, triangle winding and lane speed.
5. Inspect one full lap (about 95 s at cruise), including both bends and start-line
   crossing. No teleport, holes, camera flips, standing T-pose, missing materials.
6. Stop, save, reopen the generated scene and repeat: road meshes/materials must
   survive reload. Rebuild a second time to verify deterministic regeneration.
7. Check iPad frame rate and visuals before accepting the art pass.

## Honest acceptance state

Source/dependency CI is not a Unity compile or screenshot test. This workstation
has no Unity editor: generated scene, C# compilation and visual acceptance must
still run in Unity. Do not mark those gates passed based on green preflight.

The rider is a seated geometric prototype, not a finished animated character.
Buildings/trees use Synty City. Road geometry is purpose-built from the very same
metre-based route that drives the rider; no guessed road tile/pivot dimensions.
Generated assets persist under `Assets/StoryCycling/GeneratedLoop`.
The native iPad BLE app is untouched. Click/resistance issues remain separate.
