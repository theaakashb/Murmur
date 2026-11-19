#!/usr/bin/env python3
"""Run every distinct input of app/cleanup-corpus.json through a LocalCleanupRunner-protocol helper.

Usage: run_corpus.py CORPUS_JSON OUT_JSONL COMMAND [ARG...]
COMMAND is the Mac LocalCleanupRunner + model dir, or Murmur.Cleanup + GGUF (optionally behind ssh).
Writes one JSON line per input id: {"id", "input", "text"|"error", "ms"}, plus a first line {"load_ms"}.
Tone casual, no terms (the corpus has neither). Keep OUT_JSONL out of git: it holds model outputs.
"""
import json
import subprocess
import sys
import time

corpus, out_path, *cmd = sys.argv[1:]
cases = {}
for c in json.load(open(corpus)):
    cases.setdefault(c["input"], c["id"])  # distinct inputs, first id wins

start = time.monotonic()
proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
ready = json.loads(proc.stdout.readline())
load_ms = (time.monotonic() - start) * 1000
if ready != {"ready": True}:
    sys.exit(f"helper not ready: {ready}")
with open(out_path, "w") as out:
    out.write(json.dumps({"load_ms": round(load_ms)}) + "\n")
    for n, (text, cid) in enumerate(cases.items()):
        t = time.monotonic()
        proc.stdin.write(json.dumps({"id": n, "text": text, "tone": "casual", "terms": []}) + "\n")
        proc.stdin.flush()
        reply = json.loads(proc.stdout.readline())
        assert reply["id"] == n
        reply.update(id=cid, input=text, ms=round((time.monotonic() - t) * 1000))
        out.write(json.dumps(reply, ensure_ascii=False) + "\n")
proc.stdin.close()
proc.wait()
print(f"{len(cases)} requests, load {load_ms:.0f} ms")
