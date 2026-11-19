#!/usr/bin/env python3
"""Build the probe sample: make_sample.py <sup80.txt> <repo-root> > sample.txt (one word per line, original case).
Mix: a seeded uniform draw from the SCOWL size-80 superset, word tokens from the cleanup corpus and the
dictionary/learning/boost golden inputs, and synthetic brand-like tokens. Words only, no sentences."""
import json, random, re, sys
sup, root = sys.argv[1], sys.argv[2]
rnd = random.Random(20261119)
tok = re.compile(r"[A-Za-z][A-Za-z'’-]*[A-Za-z]|[A-Za-z]")
ok = re.compile(r"^[a-z][a-z'-]*$")
sup_words = sorted({w for w in open(sup, encoding="utf-8").read().split("\n") if ok.match(w)})
out = rnd.sample(sup_words, 14000)


def strings(o):
    if isinstance(o, str):
        yield o
    elif isinstance(o, dict):
        for v in o.values():
            yield from strings(v)
    elif isinstance(o, list):
        for v in o:
            yield from strings(v)


found = set()
for p in ["app/cleanup-corpus.json", "windows/golden/data/dictionary.json", "windows/golden/data/learning.json", "windows/golden/data/boost_filter.json"]:
    for s in strings(json.load(open(f"{root}/{p}", encoding="utf-8"))):
        found.update(t.replace("’", "'") for t in tok.findall(s))
found = sorted(found)
out += rnd.sample(found, min(4000, len(found)))
on = "b c d f g h j k l m n p qu r s t v w x z".split()
vo = "a e i o u y ai ea io ou".split()
for _ in range(2000):
    w = "".join(rnd.choice(on) + rnd.choice(vo) for _ in range(rnd.choice([2, 2, 3, 3, 4])))
    w += rnd.choice(["", "", "x", "ly", "ify", "io", "ra", "on", "is"])
    out.append(w.capitalize() if rnd.random() < .6 else w)
seen, res = set(), []
for w in out:
    if w not in seen:
        seen.add(w)
        res.append(w)
print("\n".join(res))
