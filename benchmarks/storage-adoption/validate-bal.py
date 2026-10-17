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
    allowed = ("src/Nethermind/Nethermind.State.Flat/", "src/Nethermind/Nethermind.State.Flat.Test/")
    foundation = {
        "src/Nethermind/Nethermind.Db/DbExtensions.cs",
        "src/Nethermind/Nethermind.Db.Rocks/RocksDbReader.cs",
        "src/Nethermind/Nethermind.Db.Rocks/DbOnTheRocks.BatchReads.cs",
        "src/Nethermind/Nethermind.Db.Rocks/ColumnDb.cs",
        "src/Nethermind/Nethermind.Db.Test/DbOnTheRocksTests.cs",
    }
    assert changed and all(path.startswith(allowed) or path in foundation for path in changed), changed
    consumer_files = {
        "src/Nethermind/Nethermind.State.Flat/ScopeProvider/FlatWorldStateScope.cs",
        "src/Nethermind/Nethermind.State.Flat/SnapshotBundle.BatchReads.cs",
        "src/Nethermind/Nethermind.State.Flat/ReadOnlySnapshotBundle.BatchReads.cs",
    }
    assert consumer_files.intersection(changed), changed
    Path("results/source-inputs.json").write_text(json.dumps(refs, indent=2) + "\n")
    Path("results/candidate.diff").write_text(subprocess.check_output(["git", "-C", "candidate", "diff", refs["control"], "HEAD"], text=True))
elif mode == "results":
    rows = json.loads(Path(sys.argv[2]).read_text())
    expected = {(count, shape, "warm") for count in (1, 7, 8, 9, 63, 64, 65, 512) for shape in ("accounts", "slots", "one-account", "overlay-1", "overlay-7", "overlay-8", "cache")}
    expected |= {(count, shape, "cold") for count in (8, 64, 512) for shape in ("accounts", "slots")}
    expected |= {(count, "one-account", "warm") for count in (127, 128, 129)}
    expected |= {(count, "one-account-cache", "warm") for count in (63, 64, 65, 127, 128, 129)}
    assert len(rows) == len(expected) and {(r["count"], r["shape"], r["cache"]) for r in rows} == expected
    for row in rows:
        assert len(row["samples"]) == (12 if row["cache"] == "warm" else 8)
        for sample in row["samples"]:
            assert math.isfinite(sample["nanoseconds"]) and sample["nanoseconds"] > 0
            assert math.isfinite(sample["managedBytes"]) and sample["managedBytes"] >= 0
            assert sample["operations"] > 0
    print("Verified all71 cases and every sample")
else:
    raise SystemExit("Unknown validation mode")
