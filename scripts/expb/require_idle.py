# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Refuse existing benchmark resources without changing them."""
import os
from pathlib import Path
import re
import subprocess
import sys

FILTERS = ('name=expb', 'label=expb', 'name=rpcbench-',
           'name=nethermind-rpcbench', 'name=ethcallchaos-bench', 'name=jsonbench-')


class IdleCheckError(RuntimeError):
    pass


def check_mounts(text, root):
    lines = text.splitlines()
    if not lines:
        raise IdleCheckError('Mount inventory is empty')
    for line in lines:
        fields = line.split()
        if len(fields) != 6:
            raise IdleCheckError('Mount inventory is malformed')
        target = re.sub(r'\\(040|011|012|134)', lambda m: chr(int(m[1], 8)), fields[1])
        if re.search(r'\\[0-9]{3}', target) or not Path(target).is_absolute():
            raise IdleCheckError('Mount target could not be interpreted')
        if fields[2] != 'overlay':
            continue
        target = Path(target).resolve()
        if target == root or root in target.parents:
            raise IdleCheckError('Existing benchmark overlay: preserve resources and stop')


def require_idle(data_dir):
    if not data_dir or not Path(data_dir).is_absolute():
        raise IdleCheckError('EXPB_DATA_DIR must be an absolute existing directory')
    root = Path(data_dir).resolve(strict=True)
    if not root.is_dir() or root == root.parent:
        raise IdleCheckError('EXPB_DATA_DIR is not a benchmark directory')
    for selection in FILTERS:
        for kind, args in (('containers', ['ps', '-aq']), ('networks', ['network', 'ls', '-q'])):
            try:
                result = subprocess.run(['docker', *args, '--filter', selection],
                    capture_output=True, text=True, timeout=30, check=False)
            except (OSError, subprocess.SubprocessError) as error:
                raise IdleCheckError('Could not inspect benchmark ' + kind) from error
            if result.returncode != 0:
                raise IdleCheckError('Could not inspect benchmark ' + kind)
            if result.stdout.strip():
                raise IdleCheckError('Existing benchmark ' + kind + ': preserve resources and stop')
    check_mounts(Path('/proc/self/mounts').read_text(encoding='utf-8'), root)


def main():
    try:
        require_idle(os.environ.get('EXPB_DATA_DIR', ''))
    except (IdleCheckError, OSError, ValueError):
        print('::error::Benchmark environment is not confirmed idle; preserve existing resources.', file=sys.stderr)
        return 1
    print('Benchmark resource inventory is idle')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
