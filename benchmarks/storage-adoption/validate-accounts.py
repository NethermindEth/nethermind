import json
import math
import os
from pathlib import Path
import subprocess
import sys

mode = sys.argv[1]
if mode == "sources":
    expected = {name: os.environ[name.upper() + "_REF"] for name in ("baseline", "control", "candidate")}
    for name, sha in expected.items():
        assert subprocess.check_output(["git", "-C", name, "rev-parse", "HEAD"], text=True).strip() == sha
        subprocess.run(["git", "-C", name, "diff", "--exit-code"], check=True)
        if name != "baseline":
            subprocess.run(["git", "-C", name, "merge-base", "--is-ancestor", expected["baseline"], "HEAD"], check=True)
    subprocess.run(["git", "-C", "candidate", "merge-base", "--is-ancestor", expected["control"], "HEAD"], check=True)
    control = set(subprocess.check_output(["git", "-C", "control", "diff", "--name-only", expected["baseline"], "HEAD"], text=True).splitlines())
    assert control == {
        "Directory.Packages.props", "nix/nuget-deps.json",
        "src/Nethermind/Nethermind.Db.Rocks/ColumnDb.cs",
        "src/Nethermind/Nethermind.Db.Rocks/DbOnTheRocks.BatchReads.cs",
        "src/Nethermind/Nethermind.Db.Rocks/DbOnTheRocks.cs",
        "src/Nethermind/Nethermind.Db.Rocks/RocksDbReader.cs",
        "src/Nethermind/Nethermind.Db.Test/DbOnTheRocksTests.cs",
        "src/Nethermind/Nethermind.Db/DbExtensions.cs",
        "src/Nethermind/Nethermind.Db/IBatchReadOnlyDb.cs",
        "src/Nethermind/Nethermind.Runner/packages.lock.json",
    }, control
    changed = subprocess.check_output(["git", "-C", "candidate", "diff", "--name-only", expected["control"], "HEAD"], text=True).splitlines()
    assert changed and all(path.startswith("src/Nethermind/") for path in changed), changed
    source = Path("candidate/src/Nethermind/Nethermind.Consensus/Processing/BlockCachePreWarmer.cs").read_text()
    assert "WarmupTransactionAccounts" in source and "IAccountPrewarmer" in source
    Path("results/source-inputs.json").write_text(json.dumps(expected, indent=2) + "\n")
    Path("results/candidate.diff").write_text(subprocess.check_output(["git", "-C", "candidate", "diff", expected["control"], "HEAD"], text=True))
    print("Verified master, API/package control and isolated account candidate")
elif mode == "results":
    rows = json.loads(Path(sys.argv[2]).read_text())
    expected = {(count, shape, "warm") for count in (1, 7, 8, 32, 64, 256, 512) for shape in ("distinct", "repeated", "shared", "mixed")}
    expected |= {(64, shape, "warm") for shape in ("snapshot", "tail1", "tail7")}
    expected |= {(count, "native-warm", "warm") for count in (8, 64, 512)}
    expected |= {(count, "distinct", "cold") for count in (64, 512)}
    assert len(rows) == len(expected)
    assert {(row["count"], row["shape"], row["cache"]) for row in rows} == expected
    for row in rows:
        assert 0 <= row["preflightCacheMisses"] <= 2 * row["count"]
        assert row["detailedMetrics"] == (os.environ.get("DETAILED_METRICS") == "1")
        assert len(row["samples"]) == (8 if row["cache"] == "cold" else 12)
        for sample in row["samples"]:
            assert math.isfinite(sample["nanoseconds"]) and sample["nanoseconds"] > 0
            assert sample["operations"] >= 1 and sample["managedBytes"] >= 0
            if row["cache"] == "cold": assert 0 <= sample["cacheMisses"] <= 2 * row["count"]
    print("Verified all 36 account-prewarming cases")
else:
    raise SystemExit("unknown validation mode")
