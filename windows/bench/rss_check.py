#!/usr/bin/env python3
"""Resident memory of the warm helper after one request, with and without dictionary support (macOS/Linux `ps`).

  python3 -I windows/bench/rss_check.py HELPER MODEL_DIR WAV [--vocabulary FILE --lexicon FILE]
"""
import json
import subprocess
import sys

helper, models, wav, *extra = sys.argv[1:]
for label, args in (("no vocabulary", []), ("with vocabulary", extra)):
    if label == "with vocabulary" and not extra:
        break
    p = subprocess.Popen([helper, models, "--serve"] + args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    json.loads(p.stdout.readline())
    p.stdin.write(wav + "\n"); p.stdin.flush(); p.stdout.readline()
    rss = int(subprocess.check_output(["ps", "-o", "rss=", "-p", str(p.pid)]).split()[0]) / 1024
    print(f"{label}: {rss:.0f} MB resident after one request")
    p.stdin.close(); p.wait()
