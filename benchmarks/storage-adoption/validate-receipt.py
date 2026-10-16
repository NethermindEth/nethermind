import json
import math
import os
from pathlib import Path
import subprocess
import sys

mode = sys.argv[1]
if mode == "sources":
    expected = {"baseline": os.environ["BASELINE_REF"], "control": os.environ["RECEIPT_REF"], "candidate": os.environ["RECEIPT_REF"]}
    for name, sha in expected.items():
        actual = subprocess.check_output(["git", "-C", name, "rev-parse", "HEAD"], text=True).strip()
        assert actual == sha, (name, actual, sha)
        subprocess.run(["git", "-C", name, "diff", "--exit-code"], check=True)
        version = "11.8.1.153" if name == "baseline" else "11.8.1.174"
        assert 'Include="Nethermind.RocksDbBindings" Version="[' + version + ']"' in Path(name, "Directory.Packages.props").read_text()
    subprocess.run(["git", "-C", "candidate", "merge-base", "--is-ancestor", expected["baseline"], "HEAD"], check=True)
    for file in ("Nethermind.Facade/Find/LogFinder.cs", "Nethermind.Blockchain/Receipts/ReceiptsIterator.cs", "Nethermind.Blockchain/Receipts/ReceiptsRecovery.Deferred.cs", "Nethermind.Serialization.Rlp/ReceiptArrayStorageDecoder.cs"):
        path = "src/Nethermind/" + file
        baseline = subprocess.check_output(["git", "-C", "candidate", "show", expected["baseline"] + ":" + path])
        assert baseline == Path("candidate", path).read_bytes(), path
    Path("results/source-inputs.json").write_text(json.dumps(expected, indent=2) + "\n")
    Path("results/candidate.diff").write_text(subprocess.check_output(["git", "-C", "candidate", "diff", expected["baseline"], "HEAD"], text=True))
    print("Verified common master ancestry, package versions and unchanged production receipt scanner")
elif mode == "results":
    root = Path(sys.argv[2])
    files = list((root / "results").glob("*-report-full-compressed.json"))
    assert len(files) == 1, files
    rows = json.loads(files[0].read_text())["Benchmarks"]
    shapes = ("full-dense", "compact-hashes-dense", "compact-legacy-dense", "compact-hashes-sparse", "missing", "empty")
    assert len(rows) == 24
    observed = set()
    for row in rows:
        params = row["Parameters"]
        assert row["Statistics"] and row["Statistics"]["N"] >= 6
        assert math.isfinite(row["Statistics"]["Mean"]) and row["Statistics"]["Mean"] > 0
        actual = [m for m in row["Measurements"] if m["IterationMode"] == "Workload" and m["IterationStage"] == "Actual"]
        assert len(actual) == 8
        count = next(n for n in (1,128) if "ReceiptCount=" + str(n) + "&" in params + "&")
        size = next(n for n in (32,4096) if "DataSize=" + str(n) + "&" in params + "&")
        shape = next(s for s in shapes if "Shape=" + s in params)
        observed.add((count,size,shape))
    assert observed == {(c,d,s) for c in (1,128) for d in (32,4096) for s in shapes}
    print("Verified24 receipt cases and eight actual samples per case")
else:
    raise SystemExit("Unknown receipt proof mode")
