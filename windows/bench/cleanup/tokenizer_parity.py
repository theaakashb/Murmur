#!/usr/bin/env python3
"""Token-ID parity between the MLX checkpoint's HF tokenizer and the GGUF tokenizer inside Murmur.Cleanup.

Usage: tokenizer_parity.py MLX_MODEL_DIR GGUF CORPUS_JSON [HELPER_COMMAND...]
(default helper: dotnet windows/src/Murmur.Cleanup/bin/Release/net10.0/Murmur.Cleanup.dll)

Each corpus input (and a few edge cases) is tokenized two ways: the plain string main.swift counts against the
400-token cap (system + newline + header + text) and the full ChatML prompt. Prints only counts, never text.
Run with python -I from a directory that is not the model's.
"""
import json
import subprocess
import sys

from transformers import AutoTokenizer

model_dir, gguf, corpus = sys.argv[1:4]
helper = sys.argv[4:] or ["dotnet", "windows/src/Murmur.Cleanup/bin/Release/net10.0/Murmur.Cleanup.dll"]
SYSTEM = "Clean up this dictation."

texts = sorted({c["input"] for c in json.load(open(corpus))})
texts += ["", " ", "  two  spaces ", "line one\nline two\n\n", "tabs\tand nbsp", "café naïve résumé 你好 こんにちは",
          "emoji \U0001f600 and \U0001f1fa\U0001f1f8 flag", "numbers 1,234.56 and 3.14159 and 2026-11-19", "https://example.com/a?b=c&d=e", "snake_case camelCase CONSTANT_CASE",
          "it's don't we'll ‘quoted’ “double”", "a" * 600, "word " * 300]
headers = ["", "[tone: formal]\n", "[vocab: Kubernetes; Aakash; Murmur]\n", "[tone: formal]\n[vocab: Kubernetes; Aakash]\n"]
prompts = [h + t for t in texts for h in headers[:1]] + [h + t for t in texts[:40] for h in headers[1:]]
strings = []
for p in prompts:
    strings.append(SYSTEM + "\n" + p)
    strings.append(f"<|im_start|>system\n{SYSTEM}<|im_end|>\n<|im_start|>user\n{p}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n")

tok = AutoTokenizer.from_pretrained(model_dir)
expected = [tok.encode(s) for s in strings]

proc = subprocess.run(helper + [gguf, "--tokenize"], input="".join(json.dumps(s) + "\n" for s in strings), capture_output=True, text=True, check=True)
lines = [json.loads(x) for x in proc.stdout.splitlines()]
assert lines[0] == {"ready": True}
got = [x["ids"] for x in lines[1:]]
assert len(got) == len(expected)
bad = [i for i, (a, b) in enumerate(zip(expected, got)) if a != b]
print(f"strings: {len(strings)}  identical token IDs: {len(strings) - len(bad)}  mismatches: {len(bad)}")
for i in bad[:10]:
    print(f"  #{i} hf={len(expected[i])} tokens gguf={len(got[i])} tokens, first difference at {next((k for k, (a, b) in enumerate(zip(expected[i], got[i])) if a != b), min(len(expected[i]), len(got[i])))}")
sys.exit(1 if bad else 0)
