#!/bin/zsh
# Builds the managed local cleanup helper (module-one/cleanup/Runner) from pinned sources and prints the
# release directory. mlx-swift 0.31.6 builds its Metal kernels with the Metal compiler, which the Command Line
# Tools lack, so its Cmlx target is replaced by the project's official prebuilt Cmlx.xcframework (checksum-verified).
set -euo pipefail
# app/build.sh selects the macOS 26 SDK for SwiftUI; the helper (swift-collections) needs the default SDK.
unset SDKROOT
project_root="$(cd "$(dirname "$0")/../.." && pwd)"
package="$project_root/module-one/cleanup/Runner"
deps="$project_root/runtime/cleanup-runner-deps"
mlx_revision="0bb916c67f4b9e5c682cbe02a42c701c93ab5021"
cmlx_url="https://github.com/ml-explore/mlx-swift/releases/download/0.31.6/Cmlx.xcframework.zip"
cmlx_sha="a202bf1dcfe1e64404adabfeb5eb363332e3a6221d18e4289ca0663fa3ab86c9"
if [[ ! -f "$deps/.verified" ]]; then
  rm -rf "$deps"; mkdir -p "$deps/download"
  git clone --quiet https://github.com/ml-explore/mlx-swift "$deps/mlx-swift"
  git -C "$deps/mlx-swift" -c advice.detachedHead=false checkout --quiet "$mlx_revision"
  rm -rf "$deps/mlx-swift/.git"
  curl --fail --location --retry 3 "$cmlx_url" -o "$deps/download/Cmlx.zip"
  echo "$cmlx_sha  $deps/download/Cmlx.zip" | shasum -a 256 -c -
  mkdir -p "$deps/mlx-swift/Binary"
  # ditto keeps the framework symlinks that Python's zipfile drops.
  ditto -x -k "$deps/download/Cmlx.zip" "$deps/mlx-swift/Binary"
  chmod -R u+w "$deps/mlx-swift"
  python3 - "$deps/mlx-swift/Package.swift" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
old = "    targets: [\n        cmlx,\n"
assert old in s, "mlx-swift Package.swift changed: update the Cmlx patch"
open(p, "w").write(s.replace(old, '    targets: [\n        .binaryTarget(name: "Cmlx", path: "Binary/Cmlx.xcframework"),\n'))
PY
  touch "$deps/.verified"
fi
cd "$package"
swift package edit mlx-swift --path "$deps/mlx-swift" >/dev/null 2>&1 || true
# The native build system avoids the default one's attempt to compile Metal sources.
swift build --build-system native -c release >&2
print -r -- "$(swift build --build-system native -c release --show-bin-path)"
