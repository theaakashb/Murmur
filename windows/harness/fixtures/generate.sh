#!/bin/bash
# Regenerates the WAVs and manifest.json from phrases.tsv. Needs macOS (say, afconvert) and python3.
set -euo pipefail
cd "$(dirname "$0")"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
while IFS=$'\t' read -r id spoken cleaned; do
  say -v Samantha -o "$tmp/$id.aiff" "$spoken"
  afconvert -f WAVE -d LEI16@16000 -c 1 "$tmp/$id.aiff" "$id.wav"
done < phrases.tsv
python3 - <<'PY'
import json, wave
rows = [l.rstrip("\n").split("\t") for l in open("phrases.tsv") if l.strip()]
out = []
for id_, spoken, cleaned in rows:
    w = wave.open(f"{id_}.wav")
    out.append({"id": id_, "wav": f"{id_}.wav", "spoken": spoken, "cleanup": cleaned,
                "seconds": round(w.getnframes() / w.getframerate(), 2)})
with open("manifest.json", "w") as f:
    json.dump(out, f, indent=2); f.write("\n")
PY
