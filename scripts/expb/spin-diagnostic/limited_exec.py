# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Linux-only private child launcher; avoids preexec_fn in a threaded executor."""
import importlib.util
from pathlib import Path
import os
import resource
import signal
import sys


def main():
    parent, limit, executable, *args = sys.argv[1:]
    if not executable.startswith("/") or not 1 <= int(limit) <= 2048 * 1024 * 1024:
        raise ValueError("invalid child bounds")
    os.umask(0o077)
    resource.setrlimit(resource.RLIMIT_FSIZE, (int(limit), int(limit)))
    # Isolated Python excludes the script directory from sys.path.
    spec = importlib.util.spec_from_file_location(
        "_scheduler_pidfd_compat", Path(__file__).with_name("pidfd_compat.py"))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    libc = helper.host_libc()
    # Parent death must not leave a system-wide recording running.
    if libc.prctl(1, signal.SIGINT, 0, 0, 0) != 0:
        raise OSError(helper.ctypes.get_errno(), "PR_SET_PDEATHSIG")
    if os.getppid() != int(parent):
        raise RuntimeError("launcher parent changed")
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    signal.signal(signal.SIGXFSZ, signal.SIG_DFL)
    os.execv(executable, [executable, *args])


if __name__ == "__main__":
    main()
