#!/usr/bin/env bash
set -euo pipefail

project_file="StoryCycling.xcodeproj/project.pbxproj"

plutil -lint "$project_file"
git diff --check HEAD^ HEAD

if grep -q 'DEVELOPMENT_ASSET_PATHS' "$project_file"; then
  echo "DEVELOPMENT_ASSET_PATHS must not refer to an untracked directory. Add the directory or remove the setting."
  exit 1
fi

grep -q 'PRODUCT_BUNDLE_IDENTIFIER = com.vikdiel.storycycling;' "$project_file"
grep -q 'TARGETED_DEVICE_FAMILY = 2;' "$project_file"
grep -q 'ENABLE_TESTABILITY = YES;' "$project_file"

echo "Preflight passed: project parses, diff has no whitespace errors, and iPad metadata is present."
