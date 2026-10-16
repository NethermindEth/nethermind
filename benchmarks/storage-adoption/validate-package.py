import json
import base64
import hashlib
import os
import subprocess
from pathlib import Path

version = os.environ["PACKAGE_VERSION"]
name = f"Nethermind.RocksDbBindings/{version}"
records = {}
components = os.environ["COMPONENTS"]
sources = ("history", "multiget") if components == "both" else (("multiget",) if components == "blob" else ("history",))
for source in sources:
    assets = Path(source, "src/Nethermind/artifacts/obj/Nethermind.Db.Rocks/project.assets.json")
    data = json.loads(assets.read_text())
    package = data["libraries"][name]
    assert package["type"] == "package", (source, package)
    assert not any("rocksdbbindings" in key.lower() and value["type"] == "project" for key, value in data["libraries"].items())
    changes = subprocess.check_output(["git", "-C", source, "diff", "--exit-code"], text=True)
    assert not changes
    locations = [Path(folder, package["path"]) for folder in data["packageFolders"] if Path(folder, package["path"], ".nupkg.metadata").exists()]
    assert len(locations) == 1
    folder = locations[0]
    metadata = json.loads((folder / ".nupkg.metadata").read_text())
    assert metadata["contentHash"] == package["sha512"]
    assert metadata["source"] == "https://api.nuget.org/v3/index.json"
    archive = folder / f"nethermind.rocksdbbindings.{version}.nupkg"
    archive_hash = base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
    assert archive_hash == Path(str(archive) + ".sha512").read_text().strip()
    records[source] = {"version": version, "type": package["type"], "contentHash": package["sha512"], "archiveSha512": archive_hash, "source": metadata["source"]}
assert len({(row["contentHash"], row["archiveSha512"]) for row in records.values()}) == 1
Path("results/package-consumption.json").write_text(json.dumps(records, indent=2) + "\n")
print("Verified normal published-package consumption and unchanged candidate sources")
