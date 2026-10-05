"""Verify operator/harness pins, reserve owned private state, then exec the sidecar."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import guard

HARNESS = 'feeba5b89d51fad85931c77b8d3c7fba4ff373ed'
HERE = Path(__file__).resolve().parent
SNAPSHOT = Path('/mnt/sda/nethermind-flat-25490000')
CORPUS = Path('/mnt/sda/expb-data/rpc-bench/eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz')


def git(repository, *arguments):
    return subprocess.check_output(['git','-C',str(repository),*arguments],stderr=subprocess.DEVNULL,timeout=30).decode().strip()


def launch(operator, harness):
    if sys.platform != 'linux':raise ValueError('LINUX_REQUIRED')
    run,attempt=guard.run_identity()
    if operator.resolve(strict=True)!=operator or harness.resolve(strict=True)!=harness or operator==harness:
        raise ValueError('DISTINCT_CANONICAL_CHECKOUTS_REQUIRED')
    if git(operator,'rev-parse','HEAD')!=os.environ['GITHUB_SHA'] or git(harness,'rev-parse','HEAD')!=HARNESS:
        raise ValueError('EXACT_CHECKOUTS_REQUIRED')
    if git(operator,'status','--porcelain') or git(harness,'status','--porcelain'):
        raise ValueError('CLEAN_CHECKOUTS_REQUIRED')
    if HERE != operator/'scripts/rpc-capability':raise ValueError('OPERATOR_PACKAGE_LOCATION')
    freeze=guard.read(HERE/'release-freeze.json')
    if freeze.get('status')!='READY_FOR_REVIEWED_PROSPECTIVE_CAPABILITY' or freeze.get('harness_head')!=HARNESS:
        raise ValueError('PROSPECTIVE_FREEZE_REQUIRED')
    for relative,digest in freeze['files'].items():
        path=HERE/relative
        if path.resolve(strict=True)!=path or not path.is_relative_to(HERE) or guard.digest(path)!=digest:
            raise ValueError('OPERATOR_BYTES_CHANGED')
    pins=guard.read(HERE/'expected-pins.json')
    if (pins.get('provenance_kind')!='PROSPECTIVE_FREEZE' or pins.get('inventory_sha256')!=freeze.get('inventory_sha256')
            or not re.fullmatch('[0-9a-f]{64}',pins.get('corpus_sha256',''))
            or not re.fullmatch('[0-9a-f]{64}',pins.get('snapshot_fingerprint_sha256',''))):
        raise ValueError('EXPECTED_PINS_REQUIRED')
    for relative,digest in guard.read(HERE/'native/source-pins.json').items():
        if guard.digest(harness/relative)!=digest:raise ValueError('HARNESS_BYTES_CHANGED')
    if guard.SHARED.resolve(strict=True)!=guard.SHARED or SNAPSHOT.resolve(strict=True)!=SNAPSHOT or CORPUS.resolve(strict=True)!=CORPUS:
        raise ValueError('CANONICAL_INPUT_PATHS_REQUIRED')
    if guard.SHARED==SNAPSHOT or guard.SHARED.is_relative_to(SNAPSHOT) or SNAPSHOT.is_relative_to(guard.SHARED):
        raise ValueError('PRIVATE_ROOT_SEPARATION')
    if guard.digest(CORPUS)!=pins['corpus_sha256']:raise ValueError('CORPUS_CHANGED_SINCE_FREEZE')
    if any(guard.SHARED.glob('rpc-private-*/native-ownership-hold.json')):
        raise ValueError('UNRESOLVED_PRIOR_OWNERSHIP_HOLD')
    temporary=Path(os.environ['RUNNER_TEMP']).resolve(strict=True)
    if any(temporary.glob('rpc-provenance-*/OWNERSHIP-HOLD.json')):
        raise ValueError('UNRESOLVED_INVENTORY_OWNERSHIP_HOLD')
    storage,lock=guard.paths()
    # A persistent marker survives Actions concurrency release and incomplete execution.
    lock.mkdir(mode=0o700)
    storage.mkdir(mode=0o700)
    for name in ('scratch','out','state'):(storage/name).mkdir(mode=0o700)
    marker={'schema':1,'run_id':run,'attempt':attempt,'storage':str(storage),
            'storage_identity':guard.directory(storage),'lock_identity':guard.directory(lock)}
    with (lock/'owner.json').open('x') as target:json.dump(marker,target)
    with (storage/'integration-freeze.json').open('x') as target:
        json.dump({'operator_head':os.environ['GITHUB_SHA'],'harness_head':HARNESS,'release_freeze':freeze,
                   'expected_pins':pins,'node_cpuset_requested':'','node_memory_requested':''},target,indent=2)
    environment=dict(os.environ)
    environment.update(RPC_PRIVATE_STORAGE_ROOT=str(storage),RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(guard.SHARED),
                       SCRATCH_ROOT=str(storage/'scratch'),OUT_DIR=str(storage/'out'),STATE_ROOT=str(storage/'state'),
                       DB_SOURCE=str(SNAPSHOT),JB_ETH_CALL_CORPUS_FILE=str(CORPUS),
                       RPC_NATIVE_EXPECTED_PINS_FILE=str(HERE/'expected-pins.json'),
                       RPC_NATIVE_INTEGRATION_FREEZE_FILE=str(storage/'integration-freeze.json'),NODE_CPUSET='',NODE_MEMORY='')
    os.execve(sys.executable,[sys.executable,'-B',str(HERE/'native/native_capability.py'),'--repository',str(harness)],environment)


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--operator',type=Path,required=True);parser.add_argument('--harness',type=Path,required=True);args=parser.parse_args()
    os.umask(0o077)
    try:launch(args.operator.absolute(),args.harness.absolute())
    except Exception as error:
        print('NATIVE_LAUNCH_HOLD:'+type(error).__name__,file=sys.stderr)
        raise SystemExit(1)


