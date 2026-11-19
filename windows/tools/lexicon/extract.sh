#!/bin/sh
# Usage: extract.sh <scowl-source-dir-with-scowl.db> <out-dir>
# Writes candidate lists us<N>.txt (American, variant level 1) and all<N>.txt
# (American+British -ise/-ize+Canadian) for each size, plus sup80.txt (the
# evaluation superset; never shipped, because sizes above 80 carry the UKACD terms).
set -eu
src=$1; out=$2
mkdir -p "$out"
for s in 35 40 50 55 60 70 80; do
  python3 "$src/scowl" --db "$src/scowl.db" word-list $s A 1 > "$out/us$s.txt"
  python3 "$src/scowl" --db "$src/scowl.db" word-list $s A,B,Z,C 1 > "$out/all$s.txt"
done
python3 "$src/scowl" --db "$src/scowl.db" word-list 80 A,B,Z,C 3 > "$out/sup80.txt"
wc -l "$out"/*.txt
# The shipped list: size 60, American, no abbreviations or special categories, lowercase-only.
python3 "$src/scowl" --db "$src/scowl.db" word-list 60 A 1 --wo-poses abbr --categories= > "$out/us60nac.txt"
python3 "$(dirname "$0")/normalize.py" "$out/us60nac.txt" > "$out/english-scowl-60.txt"
shasum -a 256 "$out/english-scowl-60.txt"
