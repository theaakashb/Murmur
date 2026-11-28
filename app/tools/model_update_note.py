#!/usr/bin/env python3
"""Print the release-notes line for downloadable models whose pinned revision changed (empty when none).

Usage: model_update_note.py <previous artifact-pins.json> <new artifact-pins.json>
"""
import json
import sys

# Display names as the app shows them (ModelFacts.displayName); other repositories show their own name.
NAMES = {"clouddevaakash/murmur-cleanup-2b": "Murmur Cleanup 2B"}


def changed(previous: dict, new: dict) -> list[str]:
    old, now = previous["models"], new["models"]
    # Only models the app downloads (batch_files) and that an earlier release already offered.
    return [NAMES.get(repo, repo.split("/")[1]) for repo, model in now.items()
            if "batch_files" in model and repo in old and old[repo]["revision"] != model["revision"]]


def note(names: list[str]) -> str:
    if not names:
        return ""
    joined = names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]
    return (f"**Model update:** this version uses a new {joined}. If you use it, Murmur asks you to download it "
            "after updating.")


if __name__ == "__main__":
    if sys.argv[1:] == ["--check"]:
        a = {"models": {"o/x": {"revision": "1", "batch_files": []}, "o/y": {"revision": "1"}}}
        b = {"models": {"o/x": {"revision": "2", "batch_files": []}, "o/y": {"revision": "2"}, "o/z": {"revision": "1", "batch_files": []}}}
        assert changed(a, a) == [] and note([]) == ""
        assert changed(a, b) == ["x"], changed(a, b)
        assert "new A and B." in note(["A", "B"]) and "new A, B and C." in note(["A", "B", "C"])
        print("Model update note checks passed.")
    else:
        print(note(changed(*(json.load(open(path)) for path in sys.argv[1:3]))))
