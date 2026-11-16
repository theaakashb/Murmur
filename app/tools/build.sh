#!/bin/bash
# Builds one Swift measurement tool against the app's sources and prints the executable's path:
#   "$(app/tools/build.sh measure_meeting)" HELPER MODELS ...
set -eu
. "$(dirname "$0")/../sources.sh"
out="${TMPDIR:-/tmp}/murmur-tools"; mkdir -p "$out"
swiftc -O -swift-version 5 -parse-as-library -suppress-warnings "${app_sources[@]}" "$project_root/app/tools/$1.swift" -o "$out/$1"
echo "$out/$1"
