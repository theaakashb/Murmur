#!/bin/bash
# Regenerates the files the C# side reads from the Swift source of truth: windows/golden/generate.sh [name ...]
#   windows/golden/golden_<name>.swift -> windows/golden/data/<name>.json  (expected outputs for C# tests)
#   windows/shared/export_<name>.swift -> windows/shared/<name>.json        (data the C# core loads: word lists, prompts)
# Each generator's first line is "// deps: A B ..." naming the app/*.swift files it compiles with (paths relative
# to app/, as in app/check.sh). It prints one JSON document to stdout.
# --check regenerates into a temporary folder and fails if any committed file differs (the parity check).
# macOS only: the baseline is the Mac's Foundation, not swift-corelibs-foundation on Linux.
set -eu
here="$(cd "$(dirname "$0")/.." && pwd)"
. "$here/../app/sources.sh"
[ "$(uname)" = Darwin ] || { echo "generate on macOS only" >&2; exit 1; }
check=0; if [ "${1:-}" = --check ]; then check=1; shift; fi
build="${TMPDIR:-/tmp}/murmur-golden"; mkdir -p "$build" "$here/golden/data"
sources=()
for f in "$here"/golden/golden_*.swift "$here"/shared/export_*.swift; do [ -e "$f" ] && sources+=("$f"); done
failed=0
# Golden output depends on the Mac's Swift runtime and ICU, so the toolchain that made the committed files is recorded.
toolchain="macOS $(sw_vers -productVersion), $(swift --version 2>&1 | sed -n 's/.*\(Apple Swift version [^ ]*\).*/\1/p' | head -1)"
if [ $check = 1 ] && [ "$(cat "$here/golden/toolchain.txt" 2>/dev/null)" != "$toolchain" ]; then
  echo "note: committed files were made with '$(cat "$here/golden/toolchain.txt" 2>/dev/null)', this is '$toolchain'; a difference may come from the OS, not a code change" >&2
fi
for src in "${sources[@]}"; do
  base="${src##*/}"; name="${base#golden_}"; name="${name#export_}"; name="${name%.swift}"
  if [ $# -gt 0 ] && [[ " $* " != *" $name "* ]]; then continue; fi
  case "$base" in golden_*) target="$here/golden/data/$name.json" ;; *) target="$here/shared/$name.json" ;; esac
  deps="$(sed -n '1s#^// deps:##p' "$src")"
  [ -n "$deps" ] || { echo "$src: first line must be // deps: ..." >&2; exit 1; }
  files=(); for d in $deps; do files+=("$project_root/app/$d.swift"); done
  swiftc -swift-version 5 -O "${files[@]}" "$src" -o "$build/${base%.swift}"
  (cd "$project_root" && "$build/${base%.swift}") > "$build/${base%.swift}.json"
  python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$build/${base%.swift}.json"
  if [ $check = 1 ]; then
    if cmp -s "$build/${base%.swift}.json" "$target"; then echo "same ${target#$here/}"; else echo "DIFFERS ${target#$here/}: regenerate and port the change" >&2; failed=1; fi
  else
    mv "$build/${base%.swift}.json" "$target"; echo "wrote ${target#$here/}"
  fi
done
[ $check = 1 ] || [ $# -gt 0 ] || echo "$toolchain" > "$here/golden/toolchain.txt"
exit $failed
