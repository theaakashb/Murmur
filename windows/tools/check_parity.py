#!/usr/bin/env python3
"""Fails when a Swift file that has been ported to C# changed since its port.

windows/porting.json maps each ported Swift file to the C# files that port it and the SHA-256 of the
Swift file the port matches. A Mac change to that file makes this check fail until the change is
ported and the hash is updated with --update (see windows/CONTRIBUTING.md). Synthetic: reads only
source files, no network.
"""
import hashlib, json, os, sys

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
manifest_path = os.path.join(root, "windows", "porting.json")

def digest(rel):
    with open(os.path.join(root, rel), "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()

def main(argv):
    update = "--update" in argv
    with open(manifest_path) as f:
        manifest = json.load(f)
    stale = []
    for swift, entry in sorted(manifest["ported"].items()):
        missing = [p for p in [swift] + entry["csharp"] if not os.path.exists(os.path.join(root, p))]
        if missing:
            print(f"MISSING {', '.join(missing)} (listed for {swift})"); stale.append(swift); continue
        now = digest(swift)
        if now != entry["sha256"]:
            if update:
                entry["sha256"] = now; print(f"updated {swift}")
            else:
                print(f"CHANGED {swift}: port the change to {', '.join(entry['csharp'])}, then run with --update"); stale.append(swift)
    if update:
        with open(manifest_path, "w") as f:
            json.dump(manifest, f, indent=2, sort_keys=True); f.write("\n")
        return 0
    print(f"{len(manifest['ported']) - len(stale)} of {len(manifest['ported'])} ported files in step")
    return 1 if stale else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
