#!/usr/bin/env bash
set -euo pipefail

simulator_id="$(xcrun simctl list devices available -j | python3 -c '
import json
import sys

devices = json.load(sys.stdin)["devices"]
candidates = []
for runtime, runtime_devices in devices.items():
    if "iOS" not in runtime:
        continue
    for device in runtime_devices:
        if device.get("isAvailable") and "iPad" in device["name"]:
            candidates.append((runtime, device["name"], device["udid"]))

if not candidates:
    raise SystemExit("No available iPad simulator found")

# The newest installed iOS runtime is listed last by simctl. Within it, use a stable name order.
candidates.sort()
print(candidates[-1][2])
')"

echo "Testing on iPad simulator: $simulator_id"
xcodebuild \
  -project StoryCycling.xcodeproj \
  -scheme StoryCycling \
  -sdk iphonesimulator \
  -destination "platform=iOS Simulator,id=$simulator_id" \
  test \
  CODE_SIGNING_ALLOWED=NO
