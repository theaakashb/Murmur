# Source lists for build.sh, check_ui.sh and check.sh. Source this file; it sets project_root, SDKROOT and app_sources.
project_root="$(cd "$(dirname "$0")/.." && pwd)"
# The Command Line Tools' macOS 27 SDK needs a SwiftUI macro plugin they do not ship; use their
# macOS 26 SDK when it is installed. Set SDKROOT to choose another SDK.
if command -v xcrun >/dev/null 2>&1; then
  sdk26="$(dirname "$(xcrun --show-sdk-path)")/MacOSX26.sdk"
  if [ -z "${SDKROOT:-}" ] && [ -d "$sdk26" ]; then export SDKROOT="$sdk26"; fi
fi
# The app is every Swift file in app/ except entry points: main.swift and the standalone check_*.swift.
app_sources=()
for f in "$project_root"/app/*.swift; do
  case "${f##*/}" in main.swift|check_*) ;; *) app_sources+=("$f") ;; esac
done
