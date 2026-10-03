"""Process-log quality: severity ERROR plus unconditional fatal/exception markers."""
import re

ANSI_CSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
ERROR_SEVERITY = re.compile(
    r"(?m)^(?:[^\r\n|]*\|\s*ERROR\s*\|"
    r"|\s*(?:\d{4}-\d{2}-\d{2}T\S+\s+)?ERROR(?:\s|:|$))", re.I)
FATAL_MARKERS = re.compile(r"Unhandled|Out of memory|Invalid Block|Fatal|Exception", re.I)


def process_log_ok(text):
    clean = ANSI_CSI.sub('', text)
    return bool(clean.strip()) and not (ERROR_SEVERITY.search(clean) or FATAL_MARKERS.search(clean))
