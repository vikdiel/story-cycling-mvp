#!/usr/bin/env bash
set -euo pipefail

builder="Assets/StoryCycling/Editor/CapeCrownSceneBuilder.cs"
test -f "$builder"

if rg -n 'TooMoose|Palmov|FREE_CartoonPack' Assets/StoryCycling; then
  echo "Removed free-pack references found in StoryCycling source."
  exit 1
fi

required_prefabs=(
  "Environments/SM_Env_Road_YellowLines_01.prefab"
  "Environments/SM_Env_Road_01.prefab"
  "Environments/SM_Env_Sidewalk_Straight_01.prefab"
  "Environments/SM_Env_Ocean_Tile_01.prefab"
  "Buildings/SM_Bld_Apartment_Stack_01.prefab"
  "Vehicles/SM_Veh_Car_Taxi_01.prefab"
  "Characters/Character_Female_Jacket.prefab"
)

for prefab in "${required_prefabs[@]}"; do
  test -f "Assets/Synty/PolygonCity/Prefabs/$prefab"
done

echo "Unity world preflight passed: Synty assets resolved and retired packs are not referenced."
