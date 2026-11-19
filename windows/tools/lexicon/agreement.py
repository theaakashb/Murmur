#!/usr/bin/env python3
"""agreement.py <labels.tsv> <sup80.txt> <list.txt>...  Per list: agreement with NSSpellChecker on the
lowercased words (what the folded Mac calls ask), split into the SCOWL-sized words and the rest
(names, brands, misspellings). FC = list says common, Mac says not; FN = the reverse."""
import sys
rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1], encoding="utf-8")]
sup = set(open(sys.argv[2], encoding="utf-8").read().split("\n"))
print(f"{'list':10}{'words':>8}{'agree%':>8}{'FC':>7}{'FN':>7} | non-SCOWL: n  agree%  FC")
for p in sys.argv[3:]:
    lex = set(open(p, encoding="utf-8").read().split("\n"))
    a = fc = fn = 0
    n2 = a2 = fc2 = 0
    for w, _, low in rows:
        lw = w.lower()
        mine, mac = lw in lex, low == "1"
        a += mine == mac
        fc += mine and not mac
        fn += mac and not mine
        if lw not in sup:
            n2 += 1
            a2 += mine == mac
            fc2 += mine and not mac
    print(f"{p.split('/')[-1][:-4]:10}{len(lex):8}{100*a/len(rows):8.2f}{fc:7}{fn:7} | {n2:6}{100*a2/n2:8.2f}{fc2:6}")
