import os
from pathlib import Path
import xml.etree.ElementTree as ET
project=Path("multiget/src/Nethermind/Nethermind.Db.Rocks/Nethermind.Db.Rocks.csproj")
text=project.read_text()
old='<PackageReference Include="Nethermind.RocksDbBindings" />'
assert text.count(old)==1,"Expected one unmodified normal package reference"
source=Path("bindings/src/Nethermind.RocksDbBindings/Nethermind.RocksDbBindings.csproj").resolve()
relative=os.path.relpath(source,project.parent.resolve())
project.write_text(text.replace(old,f'<ProjectReference Include="{relative}" />'))
ET.parse(project)
print("Proof-only source overlay installed; production PR keeps its normal package reference.")
