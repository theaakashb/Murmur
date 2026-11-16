#!/bin/zsh
# Builds and runs only the isolated synthetic UI fixture.
set -eu
. "$(dirname "$0")/sources.sh"
fixture_path="$project_root/runtime/UIQA.app"
mkdir -p "$fixture_path/Contents/MacOS"
swiftc -swift-version 5 -target "$swift_target" \
  "${app_sources[@]}" "$project_root/app/check_ui.swift" \
  -o "$fixture_path/Contents/MacOS/UIQA"
mkdir -p "$fixture_path/Contents/Resources"
cp -R "$project_root/app/Fonts" "$fixture_path/Contents/Resources/"
cp -R "$project_root/app/Images" "$fixture_path/Contents/Resources/"
cp "$project_root/module-one/models/artifact-pins.json" "$fixture_path/Contents/Resources/artifact-pins.json"
cp "$project_root/app/Info.plist" "$fixture_path/Contents/Info.plist"
/usr/libexec/PlistBuddy -c 'Set :CFBundleExecutable UIQA' "$fixture_path/Contents/Info.plist"
/usr/libexec/PlistBuddy -c 'Set :CFBundleIdentifier cc.clouddev.dictation.uiqa' "$fixture_path/Contents/Info.plist"
/usr/libexec/PlistBuddy -c 'Set :CFBundleName Murmur UI Check' "$fixture_path/Contents/Info.plist"
codesign --force --sign - "$fixture_path"
exec "$fixture_path/Contents/MacOS/UIQA" "$@"
