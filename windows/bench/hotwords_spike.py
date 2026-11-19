#!/usr/bin/env python3
"""Hotwords spike: does sherpa-onnx hotwords + BoostFilter fix made-up names without breaking ordinary words?

  python3 -I windows/bench/hotwords_spike.py --helper HELPER --models MODEL_DIR --spike-dir DIR \
      --fixtures windows/harness/fixtures --lexicon WORDLIST [--scores 1.5,3,5]

DIR comes from hotwords-make-audio.sh. Each configuration is one `--serve` helper process:
  raw        no vocabulary (the helper's greedy search)
  S filtered vocabulary + hotwords_score S, then BoostFilter (what the app would insert)
  S unfiltered  the same boosted decode before BoostFilter (MURMUR_ASR_UNFILTERED=1), to see what the filter blocks
Prints Markdown. Synthetic speech only.
"""
import argparse
import csv
import importlib.util
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("benchmark", os.path.join(HERE, "..", "..", "module-one", "models", "benchmark.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


def serve(helper, models, wavs, extra, env):
    p = subprocess.Popen([helper, models, "--serve"] + extra, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, env={**os.environ, **env})
    ready = json.loads(p.stdout.readline())
    assert ready.get("ready"), ready
    texts = {}
    for wav in wavs:
        p.stdin.write(wav + "\n"); p.stdin.flush()
        texts[os.path.basename(wav)] = json.loads(p.stdout.readline())["text"]
    p.stdin.close(); p.wait()
    return texts, ready


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--helper", required=True); a.add_argument("--models", required=True)
    a.add_argument("--spike-dir", required=True); a.add_argument("--fixtures", required=True)
    a.add_argument("--lexicon", required=True); a.add_argument("--scores", default="1.5,3,5")
    args = a.parse_args()
    rows = list(csv.reader(open(os.path.join(HERE, "hotwords-phrases.tsv")), delimiter="\t"))
    spike = {r[0] + ".wav": (r[1], [t for t in r[2].split(",") if t]) for r in rows}
    fixtures = {m["wav"]: m["spoken"] for m in json.load(open(os.path.join(args.fixtures, "manifest.json")))}
    wavs = [os.path.join(args.spike_dir, w) for w in spike] + [os.path.join(args.fixtures, w) for w in fixtures]
    vocab = os.path.join(args.spike_dir, "vocabulary.json")
    configs = [("raw", [], {})]
    for s in args.scores.split(","):
        extra = ["--vocabulary", vocab, "--lexicon", args.lexicon]
        configs += [(f"{s} filtered", extra, {"MURMUR_HOTWORDS_SCORE": s}), (f"{s} unfiltered", extra, {"MURMUR_HOTWORDS_SCORE": s, "MURMUR_ASR_UNFILTERED": "1"})]
    results = {}
    for name, extra, env in configs:
        results[name], ready = serve(args.helper, args.models, wavs, extra, env)
        print(f"<!-- {name}: vocabulary_terms={ready.get('vocabulary_terms')} vocabulary_error={ready.get('vocabulary_error')} -->", file=sys.stderr)

    def fixed(text, terms):
        return sum(t in text for t in terms)
    n_terms = sum(len(t) for _, t in spike.values() if t)
    print(f"| Configuration | Names spelled right (of {n_terms}) | Name clips: word edits | Trap clips: word edits | 23 fixtures: word edits (of {sum(len(benchmark.words(s)) for s in fixtures.values())}) | Ordinary words changed vs raw |")
    print("|---|---:|---:|---:|---:|---:|")
    for name, texts in results.items():
        names = sum(fixed(texts[w], t) for w, (_, t) in spike.items() if t)
        ne = sum(benchmark.edits(benchmark.words(s), benchmark.words(texts[w])) for w, (s, t) in spike.items() if t)
        te = sum(benchmark.edits(benchmark.words(s), benchmark.words(texts[w])) for w, (s, t) in spike.items() if not t)
        fe = sum(benchmark.edits(benchmark.words(s), benchmark.words(texts[w])) for w, s in fixtures.items())
        changed = sum(benchmark.words(texts[w]) != benchmark.words(results["raw"][w]) for w in list(spike) + list(fixtures) if w not in [k for k, (_, t) in spike.items() if t])
        print(f"| {name} | {names} | {ne} | {te} | {fe} | {changed} clips |")
    print("\n| Name clip | Raw | " + " | ".join(n for n in results if n != "raw") + " |")
    print("|---|---|" + "---|" * (len(results) - 1))
    for w, (s, t) in spike.items():
        if t:
            print(f"| {w} | {results['raw'][w]} | " + " | ".join(results[n][w] for n in results if n != "raw") + " |")
    print("\nClips outside the name set whose text changed against raw:")
    for name, texts in results.items():
        if name == "raw" or "unfiltered" in name:
            continue
        for w in list(spike) + list(fixtures):
            if (w in spike and spike[w][1]) or benchmark.words(texts[w]) == benchmark.words(results["raw"][w]):
                continue
            print(f"- {name}: {w}: `{results['raw'][w]}` -> `{texts[w]}`")


if __name__ == "__main__":
    main()
