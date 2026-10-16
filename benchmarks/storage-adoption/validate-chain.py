import base64
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

SOURCES = ("baseline", "control", "candidate")
VERSIONS = {"baseline": "11.8.1.153", "control": "11.8.1.174", "candidate": "11.8.1.174"}
mode = sys.argv[1]
if mode == "sources":
    expected = {name: os.environ[name.upper() + "_REF"] for name in SOURCES}
    for name in SOURCES:
        actual = subprocess.check_output(["git", "-C", name, "rev-parse", "HEAD"], text=True).strip()
        assert actual == expected[name], (name, actual)
        subprocess.run(["git", "-C", name, "diff", "--exit-code"], check=True)
        if name != "baseline":
            subprocess.run(["git", "-C", name, "merge-base", "--is-ancestor", expected["baseline"], "HEAD"], check=True)
        props = Path(name, "Directory.Packages.props").read_text()
        assert 'Include="Nethermind.RocksDbBindings" Version="[' + VERSIONS[name] + ']"' in props
        csproj = Path(name, "src/Nethermind/Nethermind.Db.Rocks/Nethermind.Db.Rocks.csproj").read_text()
        assert "RocksDbBindingsProject" not in csproj
    changed = set(subprocess.check_output(["git", "-C", "candidate", "diff", "--name-only", expected["control"], "HEAD"], text=True).splitlines())
    allowed = {"src/Nethermind/Nethermind.State/Repositories/ChainLevelInfoRepository.cs",
               "src/Nethermind/Nethermind.State.Test/Repositories/ChainLevelInfoRepositoryTests.cs",
               "src/Nethermind/Nethermind.State.Test/Nethermind.State.Test.csproj",
               "src/Nethermind/Nethermind.Benchmark/Store/ChainLevelBatchReadBenchmark.cs"}
    assert changed == allowed, changed
    Path("results/source-inputs.json").write_text(json.dumps(expected, indent=2) + "\n")
    Path("results/candidate.diff").write_text(subprocess.check_output(["git", "-C", "candidate", "diff", expected["control"], "HEAD"], text=True))
    print("Verified exact sources, shared master ancestry and isolated candidate diff")
elif mode == "packages":
    manifests = {}
    for name in SOURCES:
        subprocess.run(["git", "-C", name, "diff", "--exit-code"], check=True)
        data = json.loads(Path(name, "src/Nethermind/artifacts/obj/Nethermind.Db.Rocks/project.assets.json").read_text())
        version = VERSIONS[name]
        item = data["libraries"]["Nethermind.RocksDbBindings/" + version]
        assert item["type"] == "package"
        assert not any("rocksdbbindings" in k.lower() and v["type"] == "project" for k,v in data["libraries"].items())
        folders = [Path(f, item["path"]) for f in data["packageFolders"] if Path(f, item["path"], ".nupkg.metadata").exists()]
        assert len(folders) == 1
        folder = folders[0]
        metadata = json.loads((folder / ".nupkg.metadata").read_text())
        assert metadata["source"] == "https://api.nuget.org/v3/index.json"
        assert item["sha512"] == metadata["contentHash"]
        archive = folder / ("nethermind.rocksdbbindings." + version + ".nupkg")
        signed_hash = base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
        assert signed_hash == Path(str(archive) + ".sha512").read_text().strip()
        with zipfile.ZipFile(archive) as z:
            nuspecs = [p for p in z.namelist() if p.endswith(".nuspec")]
            assert len(nuspecs) == 1
            manifest = ET.fromstring(z.read(nuspecs[0]))
        repository = next(e for e in manifest.iter() if e.tag.rsplit("}", 1)[-1] == "repository")
        manifests[name] = {"version": version, "source": metadata["source"], "contentHash": item["sha512"], "archiveSha512": signed_hash, "repositoryCommit": repository.attrib["commit"]}
    assert manifests["control"] == manifests["candidate"]
    assert manifests["control"]["repositoryCommit"] == "31b352993ea8678348286d50420713c05fff3ccf"
    Path("results/package-consumption.json").write_text(json.dumps(manifests, indent=2) + "\n")
    print("Verified normal package consumption, hashes and matching control/candidate bindings")
elif mode == "results":
    folder = Path(sys.argv[2])
    files = list((folder / "results").glob("*-report-full-compressed.json"))
    assert len(files) == 1
    rows = json.loads(files[0].read_text())["Benchmarks"]
    cases = {f"Count={count}&HitPercent={hit}&MixedLengths={mixed}" for count in (1,2,32,256) for hit in (0,50,100) for mixed in ("False","True")}
    assert len(rows) == len(cases) and {r["Parameters"] for r in rows} == cases
    for row in rows:
        assert row["Statistics"] and row["Statistics"]["N"] >= 6
        assert math.isfinite(row["Statistics"]["Mean"]) and row["Statistics"]["Mean"] > 0
        actual = [m for m in row["Measurements"] if m["IterationMode"] == "Workload" and m["IterationStage"] == "Actual"]
        assert len(actual) == 8
    print("Verified all24 cases with eight actual samples")
else:
    raise SystemExit("Unknown validation mode")
