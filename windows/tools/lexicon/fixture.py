#!/usr/bin/env python3
"""fixture.py <labels.tsv> <sup80.txt> <lexicon.txt> <fixture-out.tsv> [<size-lists>...]
Writes the committed fixture (lowercased word<TAB>1 if NSSpellChecker finds no misspelling) and prints the
agreement of the shipped lexicon, using the same rules as Lexicon.IsCommonWord, on the fixture."""
import random, sys
labels, sup80, lexf, out = sys.argv[1:5]
lex = set(open(lexf, encoding="utf-8").read().split("\n"))
sup = set(open(sup80, encoding="utf-8").read().split("\n"))


def base(w):
    return w in lex or (w.endswith("'s") and w[:-2] in lex)


def common(w):
    return base(w) or ("-" in w and all(p and base(p) for p in w.split("-")))


seen, rows = set(), []
for l in open(labels, encoding="utf-8"):
    w, _, low = l.rstrip("\n").split("\t")
    w = w.lower()
    if w not in seen:
        seen.add(w)
        rows.append((w, low))
rnd = random.Random(7)
inside = [r for r in rows if r[0] in sup]
outside = [r for r in rows if r[0] not in sup]
fixture = sorted(rnd.sample(inside, 2500) + outside)
open(out, "w", encoding="utf-8").write("".join(f"{w}\t{m}\n" for w, m in fixture))
for name, rs in (("fixture", fixture), ("all sampled", rows)):
    ag = sum(common(w) == (m == "1") for w, m in rs)
    fc = sum(common(w) and m != "1" for w, m in rs)
    fn = sum((not common(w)) and m == "1" for w, m in rs)
    print(f"{name}: n={len(rs)} agree={ag} ({100*ag/len(rs):.2f}%) false-common={fc} false-not-common={fn}")
