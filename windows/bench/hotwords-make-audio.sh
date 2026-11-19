#!/bin/bash
# macOS only (say, afconvert). Writes the hotword spike's WAVs (never committed) and vocabulary.json into OUT_DIR.
#   windows/bench/hotwords-make-audio.sh OUT_DIR
set -euo pipefail
out="${1:?usage: hotwords-make-audio.sh OUT_DIR}"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$out"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
while IFS=$'\t' read -r id spoken terms; do
  say -v Samantha -o "$tmp/$id.aiff" "$spoken"
  afconvert -f WAVE -d LEI16@16000 -c 1 "$tmp/$id.aiff" "$out/$id.wav"
done < "$here/hotwords-phrases.tsv"
# Made-up names, plus three real words ("Notion", "Gemini", "Planit") that the trap clips' ordinary words sound a little like.
cat > "$out/vocabulary.json" <<'EOF'
{"terms":[{"text":"Brindlewick"},{"text":"Quillfeather"},{"text":"Zorbax"},{"text":"Vandermeer Tolliver"},{"text":"Kestrelmoor"},{"text":"Ondrasek"},{"text":"Fenwhistle"},{"text":"Notion"},{"text":"Gemini"},{"text":"Planit"}]}
EOF
ls "$out"
