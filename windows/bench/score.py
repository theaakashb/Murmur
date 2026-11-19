#!/usr/bin/env python3
"""Scores AsrBench JSONL files (and the Mac Core ML baseline) against the fixtures' `spoken` text.

WER uses module-one/models/benchmark.py's own `words` and `edits`, so the normalization is identical to MODEL_BENCHMARK.md's.

  python3 -I windows/bench/score.py --manifest windows/harness/fixtures/manifest.json \
      --coreml ~/murmur-m3/bench/mac-asr.jsonl mac-onnx=out/mac-onnx.jsonl vm-onnx=out/vm-onnx.jsonl

Prints Markdown. Synthetic speech only; no audio is read.
"""
import argparse
import importlib.util
import json
import os
import statistics

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("benchmark", os.path.join(HERE, "..", "..", "module-one", "models", "benchmark.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


def med(xs):
    return statistics.median(xs) if xs else float("nan")


def score(texts, reference, skip=()):
    """texts: wav -> text. Returns (word edits, reference words, exact clips, clips)."""
    edits = words = exact = 0
    for wav, spoken in reference.items():
        if wav in skip:
            continue
        ref, hyp = benchmark.words(spoken), benchmark.words(texts.get(wav, ""))
        e = benchmark.edits(ref, hyp)
        edits += e; words += len(ref); exact += e == 0
    return edits, words, exact, len(reference)


def load_jsonl(path):
    return [json.loads(l) for l in open(path) if l.strip()]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--manifest", required=True)
    p.add_argument("--coreml", help="~/murmur-m3/bench/mac-asr.jsonl")
    p.add_argument("runs", nargs="*", help="label=path.jsonl from AsrBench")
    args = p.parse_args()
    reference = {m["wav"]: m["spoken"] for m in json.load(open(args.manifest))}
    seconds = {m["wav"]: m["seconds"] for m in json.load(open(args.manifest))}
    engines = {}  # label -> dict(texts, row)

    if args.coreml:
        rows = load_jsonl(args.coreml)
        texts = {r["wav"]: r["mac"]["text"] for r in rows}
        cold = [r["mac"]["cold_transcribe_seconds"] for r in rows]
        loads = [r["mac"]["load_seconds"] for r in rows]
        engines["Mac Core ML (FluidAudio)"] = dict(texts=texts, env="Apple M3, macOS (Mac app's ModelBench)",
            load=f"{loads[0]:.2f} s first run (Core ML compile), {med(loads[1:]):.2f} s median after",
            single=f"{med(cold) * 1000:.0f} ms median / {max(cold) * 1000:.0f} ms max (cold, first transcribe in a fresh process)",
            warm="n/a (baseline is one process per clip)")

    for spec_ in args.runs:
        label, path = spec_.split("=", 1)
        rows = load_jsonl(path)
        env = next(r for r in rows if r["type"] == "environment")
        clips = [r for r in rows if r["type"] == "clip"]
        single = [r for r in clips if r["mode"] == "single"]
        warm2 = [r for r in clips if r["mode"] == "warm" and r["pass"] == 2]
        ready = next((r["wall_seconds"] for r in rows if r["type"] == "warm-ready"), float("nan"))
        failed = [r["wav"] for r in clips if not r["ok"]]
        loads = [r["helper_load_seconds"] for r in single if r["helper_load_seconds"] is not None]
        cold = [r["helper_cold_seconds"] for r in single if r["helper_cold_seconds"] is not None]
        wall_single = [r["wall_seconds"] for r in single]
        wall_warm = [r["wall_seconds"] for r in warm2]
        engines[label] = dict(
            texts={r["wav"]: r["text"] for r in warm2}, failed=failed,
            env=f'{env["cpu"]}, {env["logical_cpus"]} logical CPUs, {env["ram_gb"]} GB RAM, {env["os"]}, helper threads: {env["threads"]}',
            load=f"{med(loads):.2f} s median helper load (max {max(loads):.2f}); warm helper ready in {ready:.2f} s",
            single=f"{med(cold) * 1000:.0f} ms median / {max(cold) * 1000:.0f} ms max first transcribe; whole one-shot process {med(wall_single):.2f} s median / {max(wall_single):.2f} s max",
            warm=f"{med(wall_warm) * 1000:.0f} ms median / {max(wall_warm) * 1000:.0f} ms max per request (pass 2, wall clock through the app's WarmRunner)")
        # The single-mode transcripts must match the warm ones; a difference would be a protocol bug.
        engines[label]["single_texts"] = {r["wav"]: r["text"] for r in single}

    # The references are spoken words; every engine writes numbers as digits ("4721", "$125.50"), which counts as word errors for all of them.
    digits = {w for e in engines.values() for w, t in e["texts"].items() if any(c.isdigit() for c in t)}
    print("| Engine | Micro WER | Word edits / reference words | Exact clips | Micro WER without the clips where an engine wrote digits | Failed clips |")
    print("|---|---:|---:|---:|---:|---:|")
    for label, e in engines.items():
        edits, words, exact, n = score(e["texts"], reference)
        e2, w2, _, _ = score(e["texts"], reference, digits)
        print(f"| {label} | {edits / words * 100:.2f}% | {edits} / {words} | {exact} / {n} | {e2 / w2 * 100:.2f}% ({e2} / {w2}, {len(reference) - len(digits)} clips) | {len(e.get('failed', []))} |")
    print()
    for label, e in engines.items():
        print(f"- **{label}**: {e['env']}")
        print(f"  - Load: {e['load']}")
        print(f"  - One-shot (`--single`): {e['single']}")
        print(f"  - Warm (`--serve`): {e['warm']}")
        if "single_texts" in e:
            edits, words, exact, n = score(e["single_texts"], reference)
            print(f"  - `--single` text identical to `--serve` text on {sum(benchmark.words(e['single_texts'][w]) == benchmark.words(e['texts'][w]) for w in e['texts'])} / {len(e['texts'])} clips")
    total_seconds = sum(seconds.values())
    print(f"\nAudio: {len(reference)} clips, {total_seconds:.1f} s.")

    # Clips where engines disagree, after the benchmark's normalization.
    labels = list(engines)
    print("\n### Clips where the engines' words differ from the reference or from each other\n")
    print("| Clip | Reference | " + " | ".join(labels) + " |")
    print("|---|---|" + "---|" * len(labels))
    for wav, spoken in reference.items():
        hyps = [engines[l]["texts"].get(wav, "") for l in labels]
        norm = [benchmark.words(h) for h in hyps]
        if all(n == benchmark.words(spoken) for n in norm):
            continue
        print(f"| {wav} | {spoken} | " + " | ".join(h.replace("|", "/") for h in hyps) + " |")


if __name__ == "__main__":
    main()
