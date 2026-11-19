#!/bin/zsh
# Wrap a notarized Murmur.app in a signed, notarized drag-to-Applications DMG.
# Usage: package_dmg.sh <Murmur.app> <fresh-output-dir>
# Needs MURMUR_SIGN_IDENTITY (Developer ID Application) and MURMUR_NOTARY_PROFILE.
set -euo pipefail
app_path="$1"; output="$2"
[[ "$MURMUR_SIGN_IDENTITY" == "Developer ID Application:"* && -n "$MURMUR_NOTARY_PROFILE" ]] || { echo 'Set MURMUR_SIGN_IDENTITY (Developer ID) and MURMUR_NOTARY_PROFILE.' >&2; exit 1; }
[[ ! -e "$output" ]] || { echo 'Choose a fresh output folder.' >&2; exit 1; }
codesign --verify --deep --strict "$app_path"
xcrun stapler validate "$app_path"
stage="$output/stage"; dmg="$output/Murmur.dmg"
mkdir -p "$stage"
ditto "$app_path" "$stage/Murmur.app"
ln -s /Applications "$stage/Applications"
hdiutil create -volname Murmur -srcfolder "$stage" -format UDZO -ov "$dmg"
rm -rf "$stage"
codesign --sign "$MURMUR_SIGN_IDENTITY" --timestamp "$dmg"
xcrun notarytool submit "$dmg" --keychain-profile "$MURMUR_NOTARY_PROFILE" --wait
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl -a -t open --context context:primary-signature -vv "$dmg"
echo "Upload $dmg to the release as Murmur.dmg; the website download links to releases/latest/download/Murmur.dmg."
