import re
import sys
from pathlib import Path
text=re.sub(r"\x1b\[[0-9;]*m","",Path(sys.argv[1]).read_text())
if "Test run summary: Passed!" not in text: raise SystemExit("No passed test summary")
if not re.search(r"failed:\s*0\b",text): raise SystemExit("Tests failed or no failure count")
if not re.search(r"succeeded:\s*[1-9][0-9]*",text): raise SystemExit("No executed tests")
print("Verified nonempty successful test run")
