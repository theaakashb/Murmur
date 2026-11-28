#!/bin/zsh
# Prepare assets only. Publishing a GitHub Release is a separate, explicit action.
set -euo pipefail
project_root="$(cd "$(dirname "$0")/../.." && pwd)"
app_path="${1:-$project_root/runtime/Murmur.app}"
output="${2:-$project_root/runtime/update}"
sparkle_path="$("$project_root/app/tools/fetch_sparkle.sh" | tail -1)"
plist="$app_path/Contents/Info.plist"
feed="$(/usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$plist")"
/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$plist" >/dev/null
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$plist")"
release_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist")"
# Use the exact version tag in archive links; the appcast alone follows latest.
repository="${feed%/releases/latest/download/appcast.xml}"
[[ "$repository" != "$feed" && "$repository" == https://github.com/*/* ]] || { echo 'Invalid GitHub update feed.' >&2; exit 1; }
codesign --verify --deep --strict "$app_path"
[[ ! -e "$output" ]] || { echo 'Choose a fresh output folder for each release.' >&2; exit 1; }
mkdir -p "$output"
archive="$output/Murmur-$version.zip"
ditto -c -k --keepParent "$app_path" "$archive"
key_args=(--account "${MURMUR_SPARKLE_KEY_ACCOUNT:-Murmur}")
if [[ -n "${MURMUR_SPARKLE_KEY_FILE:-}" ]]; then key_args=(--ed-key-file "$MURMUR_SPARKLE_KEY_FILE"); fi
args=(--download-url-prefix "$repository/releases/download/v$release_version/" --maximum-deltas 0 --embed-release-notes)
notes="$output/Murmur-$version.md"
if [[ -n "${MURMUR_RELEASE_NOTES:-}" ]]; then cp "$MURMUR_RELEASE_NOTES" "$notes"; fi
# Sparkle's dialog shows these notes: name every downloadable model whose pinned revision changed since the last release.
previous_tag="$(git -C "$project_root" describe --tags --abbrev=0 --match 'v[0-9]*' --exclude "v$release_version" HEAD)" \
  || { echo 'No earlier v* release tag to compare model pins with: run git fetch --tags.' >&2; exit 1; }
model_note="$(python3 "$project_root/app/tools/model_update_note.py" \
  <(git -C "$project_root" show "$previous_tag:module-one/models/artifact-pins.json") "$app_path/Contents/Resources/artifact-pins.json")"
if [[ -n "$model_note" ]]; then
  if [[ -s "$notes" ]]; then printf '\n%s\n' "$model_note" >> "$notes"; else printf '%s\n' "$model_note" > "$notes"; fi
  echo "Release notes now say: $model_note"
fi
"$sparkle_path/bin/generate_appcast" "${args[@]}" "${key_args[@]}" "$output"
# generate_appcast checks the embedded key; explicitly verify the signature too.
signature="$(python3 - "$output/appcast.xml" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
signature = root.find('./channel/item/enclosure').attrib.get('{http://www.andymatuschak.org/xml-namespaces/sparkle}edSignature')
if not signature:
    raise SystemExit('No signed archive in appcast: check that the signing key matches SUPublicEDKey.')
print(signature)
PY
)"
"$sparkle_path/bin/sign_update" "${key_args[@]}" --verify "$archive" "$signature"
echo "Upload $archive and $output/appcast.xml to a draft release tagged v$release_version, then publish it as latest."
