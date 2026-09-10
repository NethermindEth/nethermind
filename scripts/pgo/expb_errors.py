"""Report Fusaka failures without dumping request bodies or complete node logs."""
from pathlib import Path
import re
import sys


def strip_ansi(text):
    return re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)


def summarize(text):
    text = strip_ansi(text)
    lines = [line for line in text.splitlines() if re.search(
        r"error|exception|failed|traceback|exit[_ ]code|not found|cannot|denied|invalid|caused by|"
        r"shut.?down|shutting down|container stopped|cleanup completed|cleaning up scenario|oom_killed", line, re.IGNORECASE)]
    text = "\n".join(line[:2000] for line in lines[-60:])
    text = re.sub(r"eyJ[\w-]+\.[\w-]+\.[\w-]+", "<redacted JWT>", text)
    return text or "No failure summary found; inspect the retained EXPB log on the runner."


if __name__ == "__main__":
    for name in sys.argv[1:]:
        path = Path(name)
        print(f"EXPB diagnostics: {path}")
        print(summarize(path.read_text(errors="replace")))
