#!/usr/bin/env python3
"""normalize.py <scowl-list.txt> > lexicon.txt
Keep only words SCOWL lists with no capital letter (so "Paris", "June", "GitHub" never become "common"
once the file is lowercased), unique, sorted, one per line. Everything is NFC; curly apostrophes are not expected."""
import sys, unicodedata
words = {unicodedata.normalize("NFC", w) for w in open(sys.argv[1], encoding="utf-8").read().split("\n")}
keep = sorted(w for w in words if w and w == w.lower() and " " not in w)
sys.stdout.write("\n".join(keep) + "\n")
