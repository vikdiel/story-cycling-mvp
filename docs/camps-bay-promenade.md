# Camps Bay promenade — art and motion pass

## Intent

Keep the user-confirmed ~642 m loop, acceleration, brakes and chase camera. Give
the seafront straight a recognisable setting: wide ivory beach, turquoise surf,
palms, a walkable promenade, low cafe frontage and a sandstone mountain backdrop.
The inland return is a quiet planted training road. This is a compact fictional
loop inspired by Camps Bay, **not** a map-accurate Victoria Road reconstruction.

Reference: [Cape Town Tourism — Camps Bay](https://www.capetown.travel/neighbourhood/camps-bay/)
describes the Victoria Road restaurant strip opposite the palm-fringed beach,
with the Twelve Apostles and Lion's Head behind the neighbourhood. No photos,
brand signage or new paid packs are embedded in the project.

## Open the new scene

1. Pull `unity-world`, stop Play and wait for scripts to compile.
2. **Story Cycling → Build Camps Bay Promenade**.
3. The builder generates, saves, reopens and validates
   `Assets/StoryCycling/Scenes/CampsBayPromenade.unity`, then selects it for builds.
4. Play → focus Game → **Space** for 25 km/h cruise. **W/↑** accelerates;
   **S/↓** cancels cruise and brakes. The HUD reads **CAMPS BAY • PROMENADE**.

The old saved `CapeCrownLoop` and its `GeneratedLoop` assets are not overwritten
by this menu. New assets live in `GeneratedCampsBay`. To compare, open the old
loop scene; it still runs without requiring the new animation component.

## Placement rules

- Shared `CapeCrownRoute` and road mesh are unchanged.
- Seafront road centre X=48, road edge X=53, promenade extends to X=60.
- Palms are on the beach edge; benches face the water with a clear walking strip.
- Cafe facades align at X=33.5. Models retain uniform scale, maximum 8 m height,
  15.5 m footprint in 18 m slots. No floating buildings or high-rise modules.
- Terraces occupy X=34–40; the original land-side sidewalk stays clear.
- Ocean is only seaward. Inland terrain no longer sits inside a rectangular moat.
- Mountain geometry is outside the loop, not intersecting the riding corridor.
- Synty City shops/furniture are reused. Palms, beach, ridge and bicycle are
  original geometry; the rider is still a geometric prototype, not a final avatar.

## Rider motion

Separate wheel, pedal, foot, thigh and shin transforms survive mesh batching.
Feet follow opposite points on a 17 cm crank; a two-segment geometric solution
keeps leg lengths constant. Visual cadence eases out during braking/coasting.
Wheel spin follows actual distance. The visual rig banks smoothly into curves;
the route anchor and camera remain upright, with no new steering/physics input.

Cadence is cosmetic in this keyboard demo, **not measured trainer cadence**.
The Unity BLE bridge and Click/resistance issues remain separate and untouched.

## Acceptance and honest test state

Local dependency preflight is not Unity compilation or a render test.
The implementation host has no Unity Editor, so these gates remain open until
the MacBook Editor/device run:

- Editor compiles without errors; builder completes both validation passes.
- Full lap at 25 km/h: no objects on asphalt, missing surfaces or camera jumps.
- From the start straight: fronts face road, cafes sit on ground, no overlap;
  promenade/beach/ocean/terraces are spatially distinct. Check both bends too.
- W/Space: alternate knees/feet move; S: pedalling eases out while wheels roll;
  stop: no residual motion; resume: no snap. Corners lean inward, camera stays level.
- Save/reopen and a second build retain geometry, materials and rig references.
- iPad visual/performance acceptance remains pending.

Builder assertions call the production route and pedal functions, sweep a full
crank cycle, check road clearance of prop bounds around a complete lap, reject
overlapping/ungrounded/tall frontage, and reopen the saved scene to check the rig
and meshes survived serialization. These assertions run **in Unity**, not in
the Python dependency preflight.
