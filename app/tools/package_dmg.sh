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
command -v dmgbuild >/dev/null || { echo 'Install dmgbuild 1.6.7 before packaging Murmur.' >&2; exit 1; }
dmg="$output/Murmur.dmg"
mkdir -p "$output"
dmgbuild -s "${0:A:h}/dmg_settings.py" -D "app=$app_path" -D "assets=${0:A:h}" Murmur "$dmg"
codesign --sign "$MURMUR_SIGN_IDENTITY" --timestamp "$dmg"
xcrun notarytool submit "$dmg" --keychain-profile "$MURMUR_NOTARY_PROFILE" --wait
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl -a -t open --context context:primary-signature -vv "$dmg"
echo "Upload $dmg to the release as Murmur.dmg; the website download links to releases/latest/download/Murmur.dmg."
