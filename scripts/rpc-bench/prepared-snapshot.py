#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Opt-in durable, runner-local receipt-migration fixture. No automatic retirement."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import shutil
import socket
import subprocess
import time
import threading
import urllib.request

BASELINE = 'nethermindeth/nethermind@sha256:a0bc08c6b62afdaf7c8494f8f8578f3cbfd30bed6c1c2834ea651539f79b8fc2'
CANDIDATE = 'nethermindeth/nethermind@sha256:457ea6fd38f380c59d171a240ee6ea948903dd4a34c5a699dac53ffd66d87166'
SOURCE = Path('/mnt/sda/nethermind-flat-25490000')
PARENT = Path('/mnt/sda/expb-data/rpc-prepared')
OPTIONS = 'redirect_dir=off,metacopy=off,index=off'
START = re.compile(r'Adding transaction hashes to stored receipts, blocks [0-9]+ to [0-9]+\.')
DONE = re.compile(r'Transaction hashes added to stored receipts of [0-9]+ blocks, [0-9]+ bytes added\.')
ANSI = re.compile(r'\x1b\[[0-9;]*m')
FLAGS = ['--datadir=/execution-data', '--config=mainnet', '--Init.BaseDbPath=mainnet',
         '--JsonRpc.Enabled=true', '--JsonRpc.Host=0.0.0.0', '--JsonRpc.Port=8545',
         '--JsonRpc.EnabledModules=Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug',
         '--JsonRpc.Timeout=600000', '--JsonRpc.GasCap=1000000000', '--Init.DiscoveryEnabled=false',
         '--Network.MaxActivePeers=0', '--Pruning.Mode=None', '--HealthChecks.Enabled=false',
         '--Metrics.Enabled=false', '--FlatDb.Enabled=true', '--LogIndex.Enabled=false',
         '--Receipt.StoreReceipts=true', '--Receipt.CompactReceiptStore=true',
         '--Receipt.StoreTxHashes=true', '--Receipt.DeriveFromState=false']


def run(*args, root=False, timeout=120):
    command = (['sudo', '-n'] if root and os.geteuid() != 0 else []) + list(map(str, args))
    result = subprocess.run(command, capture_output=True, timeout=timeout, check=True)
    return result.stdout.decode()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encoded(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def atomic(path, value):
    temporary = path.with_suffix('.tmp')
    with temporary.open('wb') as stream:
        stream.write(encoded(value))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)
    fd = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def guard(root):
    if root.is_symlink() or root.parent.resolve() != PARENT.resolve() or not re.fullmatch(r'getlogs-[0-9]+', root.name):
        raise ValueError('fixture must be a non-symlink getlogs-<runid> child of the dedicated persistent parent')
    if SOURCE.resolve() != SOURCE or PARENT.resolve() != PARENT:
        raise ValueError('source/persistent parent cannot contain symlinks')


def space(path):
    info = os.statvfs(path)
    reserve = 128 * 2**30 if str(path).startswith('/mnt/sda') else 2 * 2**30
    if info.f_bavail * info.f_frsize < reserve or info.f_favail < 100000:
        raise RuntimeError(f'unsafe disk/inode reserve: {path}')
    return {'path': str(path), 'available_bytes': info.f_bavail * info.f_frsize, 'available_inodes': info.f_favail}


def budget(now, finish, gate=10800):
    if now + gate + 600 + 3600 > finish:
        raise RuntimeError('insufficient preparation budget: 3h gate + 10m health + 1h reserve required')


def fingerprint(path):
    # Metadata/control-file tripwire only; never described as a full DB content hash.
    listing = run('find', path, '(', '-type', 'f', '-o', '-type', 'd', '-o', '-type', 'l', ')',
                  '-printf', '%P\t%y\t%s\t%T@\t%m\t%U:%G\t%l\n', timeout=300)
    controls = run('find', path, '-type', 'f', '(', '-name', 'CURRENT', '-o', '-name', 'IDENTITY',
                   '-o', '-name', 'MANIFEST-*', '-o', '-name', 'OPTIONS-*', ')', '-print0', timeout=300)
    rows = sorted(listing.splitlines())
    for name in sorted(filter(None, controls.split('\0'))):
        rows.append(digest(Path(name).read_bytes()) + ' ' + str(Path(name).relative_to(path)))
    return digest('\n'.join(rows).encode())


def mounted(path, readonly=None):
    entry = json.loads(run('findmnt', '-J', '-M', path, '-o', 'TARGET,FSTYPE,OPTIONS'))['filesystems'][0]
    options = entry['options'].split(',')
    if entry['target'] != str(path) or entry['fstype'] != 'overlay':
        raise RuntimeError('unexpected fixture mount identity')
    if readonly is not None and ('ro' in options) != readonly:
        raise RuntimeError('fixture mount readonly state mismatch')
    if any(option in options for option in ('volatile', 'metacopy=on', 'redirect_dir=on', 'index=on')):
        raise RuntimeError('unsafe overlay options')
    return entry


def overlay(lower, root):
    for name in ('upper', 'work', 'merged'):
        (root / name).mkdir()
    run('mount', '-t', 'overlay', 'overlay', '-o',
        f'lowerdir={lower},upperdir={root}/upper,workdir={root}/work,{OPTIONS}', root / 'merged', root=True)
    mounted(root / 'merged', False)


def toy(root):
    """Exercise this kernel/filesystem, including persisted whiteouts/opaque dirs and nesting."""
    lower = root / 'lower'
    lower.mkdir()
    (lower / 'deleted').write_text('original')
    (lower / 'replaced').write_text('old')
    (lower / 'directory').mkdir()
    (lower / 'directory' / 'hidden').write_text('hidden')
    base = root / 'base'
    base.mkdir()
    child = root / 'child'
    child.mkdir()
    overlay(lower, base)
    try:
        merged = base / 'merged'
        run('rm', merged / 'deleted', root=True)
        run('rm', '-r', merged / 'directory', root=True)
        run('mkdir', merged / 'directory', root=True)
        run('sh', '-c', 'printf new > "$1"', 'sh', merged / 'replaced', root=True)
        run('sync', '-f', merged, root=True)
        run('umount', merged, root=True)
        run('mount', '-t', 'overlay', 'overlay', '-o',
            f'lowerdir={lower},upperdir={base}/upper,workdir={base}/work,{OPTIONS}', merged, root=True)
        run('mount', '-o', 'remount,ro', merged, root=True)
        mounted(merged, True)
        if (merged / 'deleted').exists() or (merged / 'directory' / 'hidden').exists() or (merged / 'replaced').read_text() != 'new':
            raise RuntimeError('overlay persistence/whiteout/opaque-dir semantics failed')
        before = fingerprint(merged)
        overlay(merged, child)
        try:
            run('sh', '-c', 'printf child > "$1"', 'sh', child / 'merged' / 'replaced', root=True)
            run('rm', child / 'merged' / 'replaced', root=True)
        finally:
            run('umount', child / 'merged', root=True)
        if fingerprint(merged) != before or (lower / 'replaced').read_text() != 'old' or not (lower / 'deleted').exists():
            raise RuntimeError('nested child modified a lower')
        return {'whiteout': True, 'opaque_directory': True, 'replacement': True, 'remount': True, 'nested_isolation': True}
    finally:
        if subprocess.run(['mountpoint', '-q', str(base / 'merged')]).returncode == 0:
            run('umount', base / 'merged', root=True)
        # Only the uniquely owned tiny toy is retired; no prepared data is deleted here.
        run('rm', '-rf', '--', root, root=True)


def logs(container):
    result = subprocess.run(['docker', 'logs', container], capture_output=True, timeout=60, check=True)
    return ANSI.sub('', (result.stdout + result.stderr).decode(errors='replace'))


def completion(text):
    start, done = START.search(text), DONE.search(text)
    if 'Adding transaction hashes to stored receipts failed' in text:
        raise RuntimeError('receipt migration failure')
    return bool(start and done and start.start() < done.start())


def check_logs(text, reused=False):
    if re.search(r'Exception|Invalid Block|InvalidBlock|Rejected invalid|Unhandled|Fatal', text, re.I):
        raise RuntimeError('node log has a disqualifying error')
    if reused and (START.search(text) or 'Adding transaction hashes to stored receipts failed' in text):
        raise RuntimeError('prepared child attempted a new receipt migration')


def rpc(method, params=None):
    request = urllib.request.Request('http://127.0.0.1:18545', data=encoded({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params or []}), headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=60) as response:
        value = json.load(response)
    if 'error' in value or 'result' not in value:
        raise RuntimeError(f'JSON-RPC failure: {value}')
    return value['result']


def identity():
    block = rpc('eth_getBlockByNumber', ['latest', False])
    result = {'chain_id': rpc('eth_chainId'), 'number': block['number'], 'hash': block['hash'], 'client': rpc('web3_clientVersion')}
    if (result['chain_id'], result['number'], result['hash']) != ('0x1', hex(25490000), '0x1bcc8cd8ce5471e5c25f9a4b5c711ce11d37445eb24be3e0b7ee57b048000933'):
        raise RuntimeError(f'runtime identity mismatch: {result}')
    return result


def validate_responses(oracle, output):
    results = []
    if len(oracle['cases']) != 100:
        raise RuntimeError('expected frozen 100-case oracle')
    for case in oracle['cases']:
        call, expected = case['call'], case['expectation']
        result = rpc(call['method'], call['params'])
        if len(result) != expected['exact_count'] or digest(encoded(result)) != expected['ordered_result_sha256']:
            raise RuntimeError(f'prepared response mismatch: {call["name"]}')
        results.append({'name': call['name'], 'count': len(result), 'sha256': digest(encoded(result))})
    atomic(output, results)


def inspect(container):
    return json.loads(run('docker', 'inspect', container))[0]


def stop(container, output):
    run('docker', 'stop', '-t', '180', container, timeout=210)
    state = inspect(container)['State']
    text = logs(container)
    (output / 'node.log').write_text(text)
    atomic(output / 'container-final.json', inspect(container))
    check_logs(text)
    if state['Running'] or state['OOMKilled'] or state['ExitCode'] != 0 or 'All DBs closed' not in text:
        raise RuntimeError('no genuine normal shutdown: refusing publication')
    run('docker', 'rm', container)
    return text


def verify(root, expected):
    guard(root)
    raw = (root / 'ready.json').read_bytes()
    if digest(raw) != expected:
        raise RuntimeError('ready manifest hash mismatch')
    manifest = json.loads(raw)
    if manifest['status'] != 'ready' or manifest['host'] != socket.gethostname() or manifest['root'] != str(root) or manifest['source'] != str(SOURCE) or manifest['options'] != OPTIONS or manifest['flags'] != FLAGS:
        raise RuntimeError('manifest lineage/options mismatch')
    if mounted(root / 'merged', True) != manifest['mount']:
        raise RuntimeError('prepared mount options/lower identity changed')
    if fingerprint(SOURCE) != manifest['source_tripwire'] or fingerprint(root / 'merged') != manifest['prepared_tripwire']:
        raise RuntimeError('source/prepared tripwire mismatch')
    return manifest


def prepare(args):
    finish = dt.datetime.fromisoformat(args.finish.replace('Z', '+00:00')).timestamp()
    budget(time.time(), finish)
    if time.time() > dt.datetime(2026, 10, 29, 1, 30, tzinfo=dt.timezone.utc).timestamp():
        raise RuntimeError('preparation latest-start 01:30UTC passed')
    PARENT.mkdir(exist_ok=True)
    root = PARENT / ('getlogs-' + os.environ['GITHUB_RUN_ID'])
    guard(root)
    root.mkdir()  # Never reopen/overwrite an existing generation.
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    container = 'prepared-getlogs-' + os.environ['GITHUB_RUN_ID']
    atomic(root / 'owner.json', {'run': os.environ['GITHUB_RUN_ID'], 'root': str(root), 'container': container, 'host': socket.gethostname(), 'source': str(SOURCE), 'status': 'incomplete'})
    atomic(output / 'request.json', {'root': str(root), 'image': BASELINE, 'flags': FLAGS, 'finish': args.finish})
    # Evidence is read-only before choosing/creating any dataset overlay.
    (output / 'filesystem.txt').write_text(run('findmnt', '-T', SOURCE) + run('findmnt', '-T', PARENT) + run('uname', '-a') + run('df', '-B1', SOURCE, '/', PARENT))
    samples = []
    def pressure():
        samples.append({'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'scratch': space(PARENT), 'root': space(Path('/'))})
        atomic(output / 'disk.json', samples)
        if time.time() > finish - 1800:
            raise RuntimeError('preparation cutoff reached; reserving shutdown/evidence time')
    pressure()
    if os.statvfs(PARENT).f_bavail * os.statvfs(PARENT).f_frsize < 512 * 2**30:
        raise RuntimeError('preparation requires 512GiB initially free: observed ~303GiB host growth plus 128GiB floor and margin')
    containers = run('docker', 'ps', '--no-trunc')
    (output / 'occupancy.txt').write_text(containers)
    if run('docker', 'ps', '-q').strip():
        raise RuntimeError('another container is running; refusing shared-host preparation')
    toyroot = root / 'toy'
    toyroot.mkdir()
    atomic(output / 'kernel-probe.json', toy(toyroot))
    source_before = fingerprint(SOURCE)
    atomic(output / 'source-before.json', source_before)
    overlay(SOURCE, root)
    running = False
    monitoring = threading.Event()
    def monitor():
        while not monitoring.wait(10):
            try:
                pressure()
            except Exception as error:
                (output / 'pressure-failure.txt').write_text(str(error))
                os.kill(os.getpid(), signal.SIGTERM)
                return
    watcher = threading.Thread(target=monitor, daemon=True)
    watcher.start()
    try:
        run('docker', 'pull', BASELINE, timeout=600)
        run('docker', 'run', '-d', '--name', container, '--restart', 'no', '--stop-signal', 'SIGINT',
            '-p', '127.0.0.1:18545:8545', '-v', f'{root}/merged:/execution-data:rw', BASELINE, *FLAGS)
        running = True
        atomic(output / 'container-start.json', inspect(container))
        health_end = time.monotonic() + 600
        while True:
            if not inspect(container)['State']['Running']:
                raise RuntimeError('node exited during startup')
            try:
                if int(rpc('eth_blockNumber'), 16) > 0:
                    break
            except (OSError, ValueError):
                pass
            if time.monotonic() >= health_end:
                raise RuntimeError('health timeout')
            time.sleep(10)
        end = time.monotonic() + 10800
        while True:
            text = logs(container)
            (output / 'migration.log').write_text(text)
            check_logs(text)
            if not inspect(container)['State']['Running']:
                raise RuntimeError('node exited during migration')
            if completion(text):
                break
            if time.monotonic() >= end:
                raise RuntimeError('10800s migration timeout')
            time.sleep(10)
        atomic(output / 'runtime-identity.json', identity())
        validate_responses(json.loads(Path(args.oracle).read_text()), output / 'responses.json')
        final_logs = stop(container, output)
        running = False
        if not completion(final_logs):
            raise RuntimeError('completion evidence absent at shutdown')
        run('sync', '-f', root / 'merged', root=True, timeout=600)
        run('mount', '-o', 'remount,ro', root / 'merged', root=True)
        mount = mounted(root / 'merged', True)
        source_after = fingerprint(SOURCE)
        if source_before != source_after:
            raise RuntimeError('protected source changed')
        manifest = {'status': 'ready', 'schema': 1, 'root': str(root), 'source': str(SOURCE), 'host': socket.gethostname(),
                    'run': os.environ['GITHUB_RUN_ID'], 'source_sha': os.environ['GITHUB_SHA'], 'image': BASELINE,
                    'allowed_images': [BASELINE, CANDIDATE], 'compatibility_status': 'baseline-preparation-only; both-image children still required',
                    'flags': FLAGS, 'options': OPTIONS, 'mount': mount, 'source_tripwire': source_after,
                    'prepared_tripwire': fingerprint(root / 'merged'), 'tripwire_scope': 'metadata plus CURRENT/IDENTITY/MANIFEST/OPTIONS hashes; NOT full content hash',
                    'oracle_sha256': digest(Path(args.oracle).read_bytes()), 'evidence_sha256': {p.name: digest(p.read_bytes()) for p in output.iterdir() if p.is_file()},
                    'completion': 'fresh positive ordered log markers; pinned completion write precedes success log; normal exit0/All DBs closed/noOOM',
                    'retention': 'user-authorized persistent fixture; manual retirement only; do not auto-delete at deadline'}
        manifest['upper_allocated_bytes'] = int(run('du', '-sx', '-B1', root / 'upper', timeout=300).split()[0])
        monitoring.set()
        watcher.join(timeout=30)
        pressure()
        shutil.copytree(output, root / 'evidence')
        atomic(root / 'ready.json', manifest)
        atomic(output / 'READY.json', {'root': str(root), 'manifest_sha256': digest((root / 'ready.json').read_bytes()), 'manifest': manifest})
        print((output / 'READY.json').read_text(), flush=True)
    finally:
        monitoring.set()
        watcher.join(timeout=30)
        if running:
            try:
                stop(container, output)
            except Exception as error:
                (output / 'cleanup-error.txt').write_text(str(error))
                raise
        final_source = fingerprint(SOURCE)
        atomic(output / 'source-after.json', final_source)
        if final_source != source_before:
            raise RuntimeError('protected source tripwire changed during preparation/cleanup')
        if not (root / 'ready.json').exists():
            if subprocess.run(['mountpoint', '-q', str(root / 'merged')]).returncode == 0:
                run('sync', '-f', root / 'merged', root=True, timeout=300)
                run('mount', '-o', 'remount,ro', root / 'merged', root=True)
            atomic(output / 'INCOMPLETE.json', {'root': str(root), 'status': 'quarantined; NOT ready; manual recovery/retirement only'})
        # Failures remain owned/incomplete, never a ready generation and never a recursive dataset deletion.


def qualify(args):
    """Untimed 100-case compatibility on a fresh child; never updates the sealed generation."""
    manifest = verify(args.root, args.manifest_sha256)
    if args.image not in (BASELINE, CANDIDATE):
        raise ValueError('only the two pinned images are supported')
    if digest(Path(args.oracle).read_bytes()) != manifest['oracle_sha256']:
        raise RuntimeError('oracle pin mismatch')
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    parent = PARENT.parent / 'rpc-prepared-arms'
    parent.mkdir(exist_ok=True)
    child = parent / ('getlogs-' + os.environ['GITHUB_RUN_ID'])
    child.mkdir()
    container = 'prepared-child-' + os.environ['GITHUB_RUN_ID']
    running = False
    overlay(args.root / 'merged', child)
    try:
        space(PARENT)
        space(Path('/'))
        run('docker', 'pull', args.image, timeout=600)
        run('docker', 'run', '-d', '--name', container, '--restart', 'no', '--stop-signal', 'SIGINT',
            '-p', '127.0.0.1:18545:8545', '-v', f'{child}/merged:/execution-data:rw', args.image, *FLAGS)
        running = True
        end = time.monotonic() + 600
        while True:
            space(PARENT)
            space(Path('/'))
            check_logs(logs(container), reused=True)
            if not inspect(container)['State']['Running']:
                raise RuntimeError('prepared child exited')
            try:
                observed = identity()
                break
            except OSError:
                if time.monotonic() >= end:
                    raise RuntimeError('prepared child health timeout')
                time.sleep(5)
        validate_responses(json.loads(Path(args.oracle).read_text()), output / 'responses.json')
        text = stop(container, output)
        running = False
        check_logs(text, reused=True)
        verify(args.root, args.manifest_sha256)
        atomic(output / 'COMPATIBILITY.json', {'status': 'passed', 'image': args.image, 'manifest_sha256': args.manifest_sha256,
                                              'identity': observed, 'cases': 100, 'timed': False})
    finally:
        if running:
            stop(container, output)
        run('umount', child / 'merged', root=True)
        # Owned child only, after successful unmount; never follow a lazy/forced unmount with deletion.
        run('rm', '-rf', '--', child, root=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['prepare', 'verify', 'qualify'])
    parser.add_argument('--root', type=Path)
    parser.add_argument('--manifest-sha256')
    parser.add_argument('--image', default=BASELINE)
    parser.add_argument('--finish', default='2026-10-29T05:47:34Z')
    parser.add_argument('--output', default='prepared-results')
    parser.add_argument('--oracle', default=str(Path(__file__).with_name('prepared-snapshot-oracle.json')))
    args = parser.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(RuntimeError('termination requested')))
    if args.action == 'prepare':
        prepare(args)
    elif args.action == 'qualify':
        qualify(args)
    else:
        print(json.dumps(verify(args.root, args.manifest_sha256), indent=2))


if __name__ == '__main__':
    main()
