import os
import subprocess
from pathlib import Path
for source,variable in (("baseline","BASELINE_REF"),("history","HISTORY_REF"),("multiget","MULTIGET_REF")):
 sha=subprocess.check_output(["git","-C",source,"rev-parse","HEAD"],text=True).strip()
 if sha!=os.environ[variable]: raise SystemExit("Wrong checkout")
 if source!="baseline":
  packages=Path(source,"Directory.Packages.props").read_text()
  expected='Include="Nethermind.RocksDbBindings" Version="[11.8.1.171]"'
  if expected not in packages: raise SystemExit("Wrong package version")
  csproj=Path(source,"src/Nethermind/Nethermind.Db.Rocks/Nethermind.Db.Rocks.csproj").read_text()
  if "RocksDbBindingsProject" in csproj: raise SystemExit("Source override must not ship")
print("Verified exact sources and normal package references")
