import json
from pathlib import Path
import re
import subprocess
import sys

output = Path(sys.argv[1])
records = []
for argument in sys.argv[2:]:
    reference, expected = argument.rsplit("=", 1)
    assert re.fullmatch(r"[0-9a-f]{40}", expected)
    inspected = json.loads(subprocess.check_output(["docker", "image", "inspect", reference], text=True))[0]
    version = subprocess.check_output(["docker", "run", "--rm", "--network", "none", "--entrypoint",
                                      "/nethermind/Nethermind.Runner", reference, "--version"], text=True,
                                     stderr=subprocess.STDOUT)
    match = re.search(r"^Commit:\s*([0-9a-fA-F]{40})\s*$", version, re.MULTILINE)
    assert match and match[1].lower() == expected, (reference, expected, version)
    assert inspected["Architecture"] == "amd64"
    records.append({"reference": reference, "id": inspected["Id"], "commit": expected, "version": version})
    print(reference, inspected["Id"], expected)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(records, indent=2) + "\n")
