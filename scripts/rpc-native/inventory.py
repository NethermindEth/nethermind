"""Freeze prospective RPC input identity without starting a node or workload."""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys

SNAPSHOT = Path('/mnt/sda/nethermind-flat-25490000')
SHARED = Path('/mnt/sda/expb-data/rpc-bench-scratch')
CORPUS = Path('/mnt/sda/expb-data/rpc-bench/eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz')
HISTORIC_HEAD = '0x1bcc8cd8ce5471e5c25f9a4b5c711ce11d37445eb24be3e0b7ee57b048000933'
LIB_SHA = '9342749b0d82d4c7123e3b3e646777656c0bce786d81cd93d3006900b12f8d0c'
PRIVATE = None

def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()

def identity(path, directory=False):
    info = path.lstat()
    if path.resolve(strict=True) != path or not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise ValueError('CANONICAL_PATH_REQUIRED')
    if not directory and info.st_nlink != 1:
        raise ValueError('REGULAR_SINGLE_LINK_REQUIRED')
    return [info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns, info.st_uid, info.st_mode]

def command(arguments, timeout=30):
    try:
        child = subprocess.Popen(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        stdout, _ = child.communicate(timeout=timeout)
    except (OSError, subprocess.TimeoutExpired, InterruptedError, KeyboardInterrupt):
        with (PRIVATE / 'OWNERSHIP-HOLD.json').open('w') as stream:
            json.dump({'status': 'UNKNOWN_CHILD_IO_NO_NEXT_DISPATCH', 'automatic_cleanup': False}, stream)
        raise
    if child.returncode:
        raise ValueError('READ_COMMAND_FAILED')
    return stdout

def refuse_active_resources():
    names = command(['docker', 'ps', '-a', '--format', '{{.Names}}']).decode().splitlines()
    if any(name.startswith(('rpcbench-', 'nethermind-rpcbench', 'ethcallchaos-bench', 'jsonbench-', 'expb')) for name in names):
        raise ValueError('FOREIGN_BENCHMARK_RESOURCES')
    if command(['docker', 'ps', '-aq', '--filter', 'label=expb']).strip():
        raise ValueError('FOREIGN_EXPB_RESOURCES')
    if any(SHARED.glob('rpc-private-*/native-ownership-hold.json')):
        raise ValueError('UNRESOLVED_OWNERSHIP_HOLD')
    journals = [SHARED / 'cpu-state/cpu-sysfs.orig', *SHARED.glob('rpc-private-*/scratch/cpu-state/cpu-sysfs.orig')]
    if any(path.is_symlink() or (path.exists() and path.stat().st_size) for path in journals):
        raise ValueError('UNRESTORED_CPU_STATE')
    for line in Path('/proc/self/mountinfo').read_text().splitlines():
        target = line.split()[4]
        if target == str(SNAPSHOT) or target.startswith(str(SNAPSHOT) + '/') or target.startswith(str(SHARED) + '/'):
            raise ValueError('ACTIVE_SNAPSHOT_OR_SCRATCH_MOUNT')

def complete_fingerprint(path):
    data = path.read_text()
    listing, controls = data.split('# control-file-hashes\n')
    expected = set()
    for line in listing.splitlines()[2:]:
        columns = line.split('\t')
        if len(columns) != 7:
            raise ValueError('FINGERPRINT_LISTING_FORMAT')
        name = Path(columns[0]).name
        if columns[1] == 'f' and (name in ('CURRENT', 'IDENTITY') or name.startswith(('MANIFEST-', 'OPTIONS-'))):
            expected.add(columns[0])
    actual = set()
    for line in controls.splitlines():
        match = re.fullmatch('([0-9a-f]{64})  (.+)', line)
        if match is None or match[2] in actual:
            raise ValueError('FINGERPRINT_CONTROL_HASH_FORMAT')
        actual.add(match[2])
    if not expected or actual != expected:
        raise ValueError('FINGERPRINT_CONTROL_COVERAGE')

def inventory(repository, output):
    head = command(['git', '-C', str(repository), 'rev-parse', 'HEAD']).decode().strip()
    if head != os.environ['GITHUB_SHA'] or not re.fullmatch('[0-9a-f]{40}', head):
        raise ValueError('SOURCE_HEAD_REQUIRED')
    if command(['git', '-C', str(repository), 'status', '--porcelain']).strip():
        raise ValueError('CLEAN_SOURCE_REQUIRED')
    for name in ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'):
        if not re.fullmatch('[1-9][0-9]{0,19}', os.environ[name]):
            raise ValueError('RUN_ID_REQUIRED')
    lib = repository / 'scripts/rpc-bench/lib.sh'
    if digest(lib) != LIB_SHA:
        raise ValueError('FINGERPRINT_IMPLEMENTATION_CHANGED')
    snapshot_identity = identity(SNAPSHOT, directory=True)
    identity(SHARED, directory=True)
    corpus_identity = identity(CORPUS)
    if not 0 < corpus_identity[2] <= 256 * 1024 * 1024:
        raise ValueError('CORPUS_SIZE')
    refuse_active_resources()
    private = PRIVATE
    census_source = repository / 'scripts/rpc-native/corpus_census.py'
    spec = importlib.util.spec_from_file_location('corpus_census', census_source)
    census = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(census)
    with contextlib.redirect_stdout(io.StringIO()):
        census.census(CORPUS, private / 'census.json')
    first = private / 'db-first.txt'
    second = private / 'db-second.txt'
    for path in (first, second):
        command(['bash', '-c', 'set -euo pipefail; source "$1"; db_fingerprint "$2" "$3"',
                 'rpc-provenance', str(lib), str(SNAPSHOT), str(path)], timeout=240)
        if not 0 < path.stat().st_size <= 64 * 1024 * 1024:
            raise ValueError('FINGERPRINT_SIZE')
        complete_fingerprint(path)
    if first.read_bytes() != second.read_bytes() or not first.read_bytes().startswith(b'# rpc-bench fingerprint v2\n'):
        raise ValueError('SNAPSHOT_CHANGED_OR_FORMAT')
    anchor = SHARED / 'fingerprints/nethermind-flat-25490000.txt'
    anchor_present = anchor.exists()
    if anchor_present:
        identity(anchor)
        if anchor.read_bytes() != first.read_bytes():
            raise ValueError('EXISTING_ANCHOR_DIFFERS')
    if identity(CORPUS) != corpus_identity or identity(SNAPSHOT, directory=True) != snapshot_identity:
        raise ValueError('SOURCE_IDENTITY_CHANGED')
    refuse_active_resources()
    result = {'schema': 1, 'status': 'PROSPECTIVE_INVENTORY_NOT_MEASUREMENT', 'source_head': head,
              'run_id': int(os.environ['GITHUB_RUN_ID']), 'attempt': int(os.environ['GITHUB_RUN_ATTEMPT']),
              'helper_sha256': digest(Path(__file__)), 'census_helper_sha256': digest(census_source),
              'fingerprint_helper_sha256': digest(lib), 'corpus': json.loads((private / 'census.json').read_text()),
              'snapshot_fingerprint_sha256': digest(first), 'snapshot_fingerprint_bytes': first.stat().st_size,
              'snapshot_fingerprint_kind': 'v2 metadata listing plus RocksDB control file hashes; not full database hash',
              'snapshot_read_stable': True, 'existing_anchor_matched': anchor_present,
              'expected_head_hash': HISTORIC_HEAD, 'head_verified_from_snapshot': False,
              'head_source': 'retained run36420120642 timings.meta.json, chain1 height25490000',
              'resources_clear_before_after': True, 'shared_free_bytes': shutil.disk_usage(SHARED).free,
              'private_source_retained': True, 'raw_request_or_database_content_exported': False,
              'historical_corpus_equivalence_claimed': False, 'historical_fingerprint_equivalence_claimed': False}
    with output.open('x') as stream:
        json.dump(result, stream, indent=2)
    print('PROSPECTIVE_INVENTORY_COMPLETE_NO_WORKLOAD')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    os.umask(0o077)
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(InterruptedError()))
    signal.signal(signal.SIGINT, lambda *_: (_ for _ in ()).throw(InterruptedError()))
    try:
        for name in ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'):
            if not re.fullmatch('[1-9][0-9]{0,19}', os.environ[name]):
                raise ValueError('RUN_ID_REQUIRED')
        temporary = Path(os.environ['RUNNER_TEMP']).resolve(strict=True)
        PRIVATE = temporary / ('rpc-provenance-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'])
        PRIVATE.mkdir(mode=0o700)
        inventory(args.repository.resolve(strict=True), args.out)
    except Exception as error:
        print('PROVENANCE_INVENTORY_FAILED:' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
