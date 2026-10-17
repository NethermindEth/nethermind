import json
import math
import os
from pathlib import Path
import subprocess
import sys

mode = sys.argv[1]
if mode == "sources":
    refs = {name: os.environ[name.upper() + "_REF"] for name in ("baseline", "control", "candidate")}
    for name, expected in refs.items():
        actual = subprocess.check_output(["git", "-C", name, "rev-parse", "HEAD"], text=True).strip()
        assert actual == expected, (name, actual, expected)
        subprocess.run(["git", "-C", name, "diff", "--exit-code"], check=True)
        if name != "baseline":
            subprocess.run(["git", "-C", name, "merge-base", "--is-ancestor", refs["baseline"], "HEAD"], check=True)
    subprocess.run(["git", "-C", "candidate", "merge-base", "--is-ancestor", refs["control"], "HEAD"], check=True)
    changed = subprocess.check_output(["git", "-C", "candidate", "diff", "--name-only", refs["control"], "HEAD"], text=True).splitlines()
    allowed = ("src/Nethermind/Nethermind.State.Flat/", "src/Nethermind/Nethermind.State.Flat.Test/", "src/Nethermind/Nethermind.Trie/", "src/Nethermind/Nethermind.Trie.Test/")
    foundation = {
        "src/Nethermind/Nethermind.Db/DbExtensions.cs",
        "src/Nethermind/Nethermind.Db.Rocks/RocksDbReader.cs",
        "src/Nethermind/Nethermind.Db.Rocks/DbOnTheRocks.BatchReads.cs",
        "src/Nethermind/Nethermind.Db.Test/DbOnTheRocksTests.cs",
    }
    assert changed and all(path.startswith(allowed) or path in foundation for path in changed), changed
    assert "src/Nethermind/Nethermind.Trie/PatriciaTree.WarmUp.cs" in changed
    Path("results/source-inputs.json").write_text(json.dumps(refs, indent=2) + "\n")
    Path("results/candidate.diff").write_text(subprocess.check_output(["git", "-C", "candidate", "diff", refs["control"], "HEAD"], text=True))
elif mode == "results":
    rows = json.loads(Path(sys.argv[2]).read_text())
    expected = {(count, shape, cache) for count in (1, 8, 64, 65, 512) for shape in ("state", "storage", "shared") for cache in ("warm-db", "warm-trie", "cold-db")}
    assert len(rows) == len(expected) and {(r["count"], r["shape"], r["cache"]) for r in rows} == expected
    for row in rows:
        assert len(row["samples"]) == (12 if row["cache"] != "cold-db" else 8)
        for sample in row["samples"]:
            assert math.isfinite(sample["nanoseconds"]) and sample["nanoseconds"] > 0
            assert math.isfinite(sample["managedBytes"]) and sample["managedBytes"] >= 0
            assert sample["operations"] > 0
    print("Verified all45 cases and every sample")
else:
    raise SystemExit("Unknown validation mode")
