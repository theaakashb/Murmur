# English lexicon (Windows)

`english-scowl-60.txt.gz` is the word list behind `Lexicon.English.IsCommonWord` in `Murmur.Core`. It stands in for the
Mac's `NSSpellChecker` check (`isCommonEnglishWord`), so dictionary respelling, learning and BoostFilter give the same
answers on every PC and in CI. It is pinned like a model: change it only by repeating the steps below.

| | |
|---|---|
| Source | SCOWL (Spell Checker Oriented Word Lists), https://github.com/en-wl/wordlist, tag `rel-2026.02.25` (tag object `7e99edab8e32f9f9ea2b15f249ca8d4d67237410`) |
| Source archive | `https://github.com/en-wl/wordlist/archive/refs/tags/rel-2026.02.25.tar.gz`, SHA-256 `74e7cc3e9e03e609c1c74bb7e8862fcd988cdd64768dcbee4611581b7e633852` |
| Selection | size 60, American spelling (`A`), variant level 1, no abbreviations (`--wo-poses abbr`), no special categories (`--categories=`), then only words SCOWL lists with no capital letter |
| Words | 88,210 (one lowercase UTF-8 word per line, sorted, `\n`) |
| Uncompressed | 849,050 bytes, SHA-256 `c9285370ac81e6c7a5a79be8d4c6023e8b63d228fd703c1870a6813bbf193d9a` |
| Shipped file | `english-scowl-60.txt.gz` (`gzip -9n`), 234,209 bytes, SHA-256 `ffc9f45f19a9fde10fedbc6a3a34c099e4bcd99d9414f04b196040e47b59f682` |
| License | `LICENSE` here: the SCOWL copyright notice, verbatim up to the `===` line. Size 60 American has no AU or UKACD part (UKACD applies only above size 80, AU only to the `D`/`AU` lists), so nothing more applies. |

Rebuild: `make` in the unpacked archive (needs Python 3, SQLite), then `windows/tools/lexicon/extract.sh <archive-dir> <out-dir>`,
which also writes the shipped list (the SHA-256 it prints must match the table); then `gzip -9n`. Measure with
`make_sample.py`, `probe.swift`, `agreement.py` and `fixture.py` in the same folder.

## Choices, measured against NSSpellChecker on this Mac

- Probe: `windows/tools/lexicon/probe.swift` asks `NSSpellChecker` (`language: "en"`, as the app does) about 17,581 words:
  14,000 drawn uniformly from the SCOWL size-80 superset (all spellings), word tokens from `app/cleanup-corpus.json` and the
  dictionary, learning and boost-filter golden inputs, and 2,000 synthetic brand-like tokens (`make_sample.py`, seeded).
  The Mac passes folded (lowercase) words, so agreement is measured on lowercased words (17,289 after de-duplication).
- NSSpellChecker is American: British `-ise`, `-our` forms (sympathising, colour-like) are misspellings, so British and
  Canadian spellings are not in the list.
- It accepts roughly 98% of the words first appearing at SCOWL size 35, 96% at 40, 94% at 50, 91% at 60, 57% at 70 and
  42% at 80, and 3% of words outside size 80. Size 60 is the largest size that is still ordinary words.
- Proper nouns: SCOWL 60 lists Paris, Jessica, June, GitHub and similar. NSSpellChecker rejects them lowercase, so only
  words SCOWL itself lists in lowercase are kept. Names, brand names and `ChatGPT`-style tokens are never common.

Agreement with NSSpellChecker on the lowercased sample, direct lookup, American lists (FC = list says common, the Mac
says not; FN = the reverse; the sample leans towards rare words, which is where sizes differ):

| List | Words | Agree | FC | FN |
|---|---:|---:|---:|---:|
| size 35 | 43,621 | 60.43% | 9 | 6,947 |
| size 40 | 49,150 | 62.93% | 11 | 6,507 |
| size 50 | 69,662 | 70.61% | 43 | 5,124 |
| size 60 | 88,854 | 77.42% | 116 | 3,853 |
| size 70 | 137,325 | 80.95% | 1,431 | 1,919 |
| size 80 | 200,386 | 77.49% | 3,843 | 114 |
| size 60, no abbreviations, no special categories (shipped) | 88,210 | 77.40% | 88 | 3,886 |

Size 70 agrees slightly more overall, but it makes 12 times as many false "common" answers, and a false "common" is the
costly one: a name or term the dictionary should respell would be protected. Size 60 is the shipped choice. Of the 87 false
"common" words at the shipped size none is a name or a brand: they are British or slang words and rare inflections
(demist, skive, sunbed, tenners, subgenres, deplatformed, coworking). The 2,320 sampled words outside SCOWL, which is
where names, brands and misspellings fall, agree 96.85% with zero false "common"; the 3% disagreements are words
the Mac knows that SCOWL does not (api, roadmap, standup, e-mail, user-style names).

With the rules below, on the committed fixture (`windows/tests/Murmur.Core.Tests/LexiconNSSpellChecker.tsv`, 4,756
lowercased words with the Mac's verdicts: 2,500 drawn from the SCOWL superset plus every sampled word outside it): 4,065
agree (85.47%), 15 false "common", 676 false "not common". On the whole sample: 13,364 / 17,289 (77.30%), 87 false "common".

## Matching rules (`Lexicon.IsCommonWord`)

Case-insensitive (lowercased, typographic apostrophe folded to `'`). A word is common when it is in the list, or when it
ends in `'s` and the part before is in the list, or when it contains hyphens and every part is common. The hyphen rule
comes from measurement: NSSpellChecker accepts a hyphenated word when its parts are words (25 of 27 sampled hyphenated
words are common to it; the rule agrees on 23 with 0 false "common" and 4 false "not common", e.g. `ai-based`). The `'s`
rule raised agreement on apostrophe words from 764 to 779 of 919 with no new false "common".

## Known disagreements

- Words the Mac accepts that SCOWL 60 lacks: rare inflections and technical words (SCOWL 70/80 territory), acronyms and
  tech words (api, sql, json, roadmap, standup, cafe, ok), and some given names that appear in the Mac's lexicon
  (Sam, Grace, Claude in the Mac's check as written).
- Words SCOWL 60 has that the Mac rejects: British or slang forms (demist, skive, sunbed), a few clipped words.
- NSSpellChecker is learned per user (words the user added, system-wide learned words); this list is fixed.
