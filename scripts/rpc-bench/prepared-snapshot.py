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
RPC_PORT = 18545
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
        raise RuntimeError(f'insufficient preparation budget: {gate}s gate + 10m health + 1h reserve required')


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


def overlay_off_options(entry):
    """findmnt omits options equal to module defaults; unknown defaults fail closed."""
    options = entry['options'].split(',')
    if 'volatile' in options:
        raise RuntimeError('unsafe volatile overlay')
    defaults = {}
    for name in ('redirect_dir', 'metacopy', 'index'):
        values = [option.split('=', 1)[1] for option in options if option.startswith(name + '=')]
        if values:
            if name == 'redirect_dir' and values == ['follow']:
                # redirect_dir=off maps to follow when this kernel policy is enabled.
                value = Path('/sys/module/overlay/parameters/redirect_always_follow').read_text().strip()
                defaults['redirect_always_follow'] = value
                if value not in ('Y', '1'):
                    raise RuntimeError('redirect_dir=follow lacks verified kernel off→follow policy')
            elif values != ['off'] and not (name == 'redirect_dir' and values == ['nofollow']):
                raise RuntimeError(f'unsafe overlay option: {name}={values}')
        else:
            value = Path('/sys/module/overlay/parameters', name).read_text().strip()
            defaults[name] = value
            if value not in ('N', '0'):
                raise RuntimeError(f'unknown or unsafe overlay default: {name}={value}')
    return defaults


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
        reported_mount = mounted(merged, True)
        print('Kernel explicit-off overlay mount: ' + json.dumps(reported_mount, sort_keys=True), flush=True)
        reported_defaults = overlay_off_options(reported_mount)
        print('Kernel explicit-off overlay defaults: ' + json.dumps(reported_defaults, sort_keys=True), flush=True)
        if (merged / 'deleted').exists() or (merged / 'directory' / 'hidden').exists() or (merged / 'replaced').read_text() != 'new':
            raise RuntimeError('overlay persistence/whiteout/opaque-dir semantics failed')
        run('mount', '-o', 'remount,rw', merged, root=True)
        mounted(merged, False)
        run('sh', '-c', 'printf recovered > "$1"', 'sh', merged / 'replaced', root=True)
        run('sync', '-f', merged, root=True)
        run('mount', '-o', 'remount,ro', merged, root=True)
        mounted(merged, True)
        if (merged / 'replaced').read_text() != 'recovered':
            raise RuntimeError('in-place recovery remount lost replacement')
        before = fingerprint(merged)
        overlay(merged, child)
        try:
            run('sh', '-c', 'printf child > "$1"', 'sh', child / 'merged' / 'replaced', root=True)
            run('rm', child / 'merged' / 'replaced', root=True)
        finally:
            run('umount', child / 'merged', root=True)
        if fingerprint(merged) != before or (lower / 'replaced').read_text() != 'old' or not (lower / 'deleted').exists():
            raise RuntimeError('nested child modified a lower')
        return {'whiteout': True, 'opaque_directory': True, 'replacement': True, 'remount': True, 'nested_isolation': True,
                'mount': reported_mount, 'omitted_option_defaults': reported_defaults}
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
    if reused and (START.search(text) or DONE.search(text) or 'Adding transaction hashes to stored receipts failed' in text):
        raise RuntimeError('prepared child attempted a new receipt migration')


def rpc(method, params=None):
    request = urllib.request.Request(f'http://127.0.0.1:{RPC_PORT}', data=encoded({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params or []}), headers={'Content-Type': 'application/json'})
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


def normal_shutdown(observed, text):
    check_logs(text)
    state = observed['State']
    exit_ok = state['ExitCode'] == 0 or (state['ExitCode'] == 130 and observed.get('Config', {}).get('StopSignal') == 'SIGINT')
    if (state['Running'] or state['OOMKilled'] or state.get('Error', '') or not exit_ok
            or 'All DBs closed' not in text or 'Nethermind is shut down' not in text):
        raise RuntimeError('no genuine normal shutdown: refusing publication')


def stop(container, output):
    run('docker', 'stop', '-t', '180', container, timeout=210)
    observed = inspect(container)
    text = logs(container)
    (output / 'node.log').write_text(text)
    atomic(output / 'container-final.json', observed)
    normal_shutdown(observed, text)
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


def start_watchdog(check, output):
    monitoring = threading.Event()
    def monitor():
        while not monitoring.wait(10):
            try:
                check()
            except Exception as error:
                (output / 'pressure-failure.txt').write_text(str(error))
                os.kill(os.getpid(), signal.SIGTERM)
                return
    watcher = threading.Thread(target=monitor, daemon=True)
    watcher.start()
    return monitoring, watcher


def recovery_preflight(root, evidence, oracle):
    """Only the independently reviewed failed producer is recoverable; no inferred provenance."""
    guard(root)
    if root != PARENT / 'getlogs-40815701914' or socket.gethostname() != 'core-ethereum-mainnet-snapshots-0':
        raise RuntimeError('recovery root/host is not the reviewed quarantine')
    if (root / 'ready.json').exists() or root.is_symlink():
        raise RuntimeError('recovery cannot overwrite a ready generation')
    files = {p.name: digest(p.read_bytes()) for p in evidence.iterdir() if p.is_file()}
    if digest(encoded(files)) != 'cdbed476f6180334e8d4f32eb56921936626fd15a05922202f7f1f84a2c4dac8':
        raise RuntimeError('old artifact contents do not match reviewed artifact 12881330426')
    load = lambda name: json.loads((evidence / name).read_text())
    expected_owner = {'run': '40815701914', 'root': str(root), 'container': 'prepared-getlogs-40815701914',
                      'host': socket.gethostname(), 'source': str(SOURCE), 'status': 'incomplete'}
    if json.loads((root / 'owner.json').read_text()) != expected_owner:
        raise RuntimeError('quarantine ownership changed')
    observed = load('container-final.json')
    text = (evidence / 'node.log').read_text()
    normal_shutdown(observed, text)
    if not completion(text) or not completion((evidence / 'migration.log').read_text()):
        raise RuntimeError('old fresh positive migration completion missing')
    request = load('request.json')
    if request['root'] != str(root) or request['image'] != BASELINE or request['flags'] != FLAGS:
        raise RuntimeError('old request differs from recovery configuration')
    if observed['Config']['Image'] != BASELINE or observed['Config']['Cmd'] != FLAGS:
        raise RuntimeError('old effective configuration mismatch')
    binds = [m for m in observed['Mounts'] if m['Destination'] == '/execution-data']
    if len(binds) != 1 or binds[0]['Source'] != str(root / 'merged') or not binds[0]['RW']:
        raise RuntimeError('old bind mismatch')
    cases = json.loads(oracle.read_text())['cases']
    expected_responses = [{'name': c['call']['name'], 'count': c['expectation']['exact_count'],
                           'sha256': c['expectation']['ordered_result_sha256']} for c in cases]
    if len(cases) != 100 or load('responses.json') != expected_responses:
        raise RuntimeError('old oracle results mismatch')
    old_identity = load('runtime-identity.json')
    if old_identity != {'chain_id': '0x1', 'number': hex(25490000),
                        'hash': '0x1bcc8cd8ce5471e5c25f9a4b5c711ce11d37445eb24be3e0b7ee57b048000933',
                        'client': 'Nethermind/v2.3.0-preview+14f76051-f/linux-x64/dotnet10.0.14'}:
        raise RuntimeError('old runtime identity mismatch')
    before = load('source-before.json')
    if before != load('source-after.json') or fingerprint(SOURCE) != before:
        raise RuntimeError('protected source differs from old producer tripwire')
    mount = mounted(root / 'merged', True)
    defaults = overlay_off_options(mount)
    required = {f'lowerdir={SOURCE}', f'upperdir={root}/upper', f'workdir={root}/work'}
    if not required.issubset(set(mount['options'].split(','))):
        raise RuntimeError('quarantine mount lineage/options mismatch; never recreate missing mounts')
    for name in ('upper', 'work', 'merged', 'owner.json'):
        if (root / name).is_symlink():
            raise RuntimeError('quarantine contains a symlinked control path')
    all_mounts = run('findmnt', '-rn', '-o', 'TARGET,OPTIONS')
    if any(str(root / 'merged') in line and not line.startswith(str(root / 'merged') + ' ') for line in all_mounts.splitlines()):
        raise RuntimeError('quarantine has child mounts/busy lowers')
    busy = subprocess.run(['sudo', '-n', 'fuser', '-m', str(root / 'merged')], capture_output=True, timeout=30)
    if busy.returncode != 1:
        raise RuntimeError('quarantine has busy processes or cannot establish quiescence')
    return {'producer_run': '40815701914', 'producer_source_sha': '8a867cce8953d3d8f18821586db336aef19bafaa', 'artifact_id': '12881330426',
            'artifact_zip_sha256': '9e220699c61df86cbdb83968d0c71208bb1a8ccf1f709f52794ed79d21e9afe9',
            'artifact_contents_sha256': digest(encoded(files)), 'old_runtime': old_identity,
            'old_source_tripwire': before, 'old_mount': mount, 'omitted_option_defaults': defaults}


def prepare(args):
    recovering = args.action == 'recover'
    finish = dt.datetime.fromisoformat(args.finish.replace('Z', '+00:00')).timestamp()
    budget(time.time(), finish, gate=3600 if recovering else 10800)
    PARENT.mkdir(exist_ok=True)
    root = args.root if recovering else PARENT / ('getlogs-' + os.environ['GITHUB_RUN_ID'])
    guard(root)
    if not recovering:
        root.mkdir()  # Never reopen/overwrite an existing generation.
    elif not root.is_dir():
        raise RuntimeError('quarantine is missing; fresh preparation requires separate authorization')
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    container = 'prepared-getlogs-' + os.environ['GITHUB_RUN_ID']
    if not recovering:
        atomic(root / 'owner.json', {'run': os.environ['GITHUB_RUN_ID'], 'root': str(root), 'container': container, 'host': socket.gethostname(), 'source': str(SOURCE), 'status': 'incomplete'})
    recovery = recovery_preflight(root, Path(args.recovery_evidence), Path(args.oracle)) if recovering else None
    if recovery:
        record = dict(recovery, run=os.environ['GITHUB_RUN_ID'], source_sha=os.environ['GITHUB_SHA'], container=container,
                      root=str(root), host=socket.gethostname(), status='incomplete')
        atomic(output / 'RECOVERY.json', record)
        atomic(root / ('recovery-' + os.environ['GITHUB_RUN_ID'] + '.json'), record)
    atomic(output / 'request.json', {'root': str(root), 'image': BASELINE, 'flags': FLAGS, 'finish': args.finish})
    # Evidence is read-only before choosing/creating any dataset overlay.
    (output / 'filesystem.txt').write_text(run('findmnt', '-T', SOURCE) + run('findmnt', '-T', PARENT) + run('uname', '-a') + run('df', '-B1', SOURCE, '/', PARENT))
    samples = []
    cutoff = min(finish - 1800, time.time() + 3600) if recovering else finish - 1800
    atomic(output / 'disk-plan.json', {'mode': 'recovery' if recovering else 'fresh', 'initial_scratch_min_gib': 128 if recovering else 512,
                                     'continuous_scratch_min_gib': 128, 'root_min_gib': 2, 'min_free_inodes': 100000,
                                     'work_cutoff_utc': dt.datetime.fromtimestamp(cutoff, dt.timezone.utc).isoformat(),
                                     'shutdown_seconds': 180, 'reserve_seconds': 3600})
    def pressure():
        samples.append({'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'scratch': space(PARENT), 'root': space(Path('/'))})
        atomic(output / 'disk.json', samples)
        if time.time() > cutoff:
            raise RuntimeError('preparation cutoff reached; reserving shutdown/evidence time')
    pressure()
    if not recovering and os.statvfs(PARENT).f_bavail * os.statvfs(PARENT).f_frsize < 512 * 2**30:
        raise RuntimeError('preparation requires 512GiB initially free: observed ~303GiB host growth plus 128GiB floor and margin')
    containers = run('docker', 'ps', '--no-trunc')
    (output / 'occupancy.txt').write_text(containers)
    if run('docker', 'ps', '-q').strip():
        raise RuntimeError('another container is running; refusing shared-host preparation')
    toyroot = root / 'toy'
    toyroot.mkdir()
    print(f'Preflight reserves/occupancy passed; testing durable OverlayFS on {socket.gethostname()}', flush=True)
    atomic(output / 'kernel-probe.json', toy(toyroot))
    print('Kernel whiteout/opaque/remount/nested-child probe passed; fingerprinting protected source', flush=True)
    source_before = fingerprint(SOURCE)
    atomic(output / 'source-before.json', source_before)
    if not recovering:
        overlay(SOURCE, root)
    running = False
    monitoring, watcher = start_watchdog(pressure, output)
    try:
        if recovering:
            run('mount', '-o', 'remount,rw', root / 'merged', root=True)
            mounted(root / 'merged', False)
        run('docker', 'pull', BASELINE, timeout=600)
        running = True  # Ownership is persisted before container creation, including interrupted docker run.
        run('docker', 'run', '-d', '--name', container, '--restart', 'no', '--stop-signal', 'SIGINT',
            '-p', '127.0.0.1:18545:8545', '-v', f'{root}/merged:/execution-data:rw', BASELINE, *FLAGS)
        observed = inspect(container)
        atomic(output / 'container-start.json', observed)
        if observed['Config']['Image'] != BASELINE or observed['Config']['Cmd'] != FLAGS or observed['Config'].get('StopSignal') != 'SIGINT':
            raise RuntimeError('fresh effective configuration mismatch')
        print(f'Owned baseline container started: {container}; readiness timeout600s', flush=True)
        health_end = time.monotonic() + 600
        while True:
            check_logs(logs(container), reused=recovering)
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
        print('Recovery: migration forbidden' if recovering else 'Waiting for fresh positive migration completion, timeout10800s', flush=True)
        end = time.monotonic() + 10800
        while not recovering:
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
        print('Checking runtime identity and100case exact oracle', flush=True)
        current_identity = identity()
        atomic(output / 'runtime-identity.json', current_identity)
        if recovering and current_identity != recovery['old_runtime']:
            raise RuntimeError('recovery runtime differs from verified old baseline')
        validate_responses(json.loads(Path(args.oracle).read_text()), output / 'responses.json')
        final_logs = stop(container, output)
        running = False
        if recovering:
            check_logs(final_logs, reused=True)
        elif not completion(final_logs):
            raise RuntimeError('completion evidence absent at shutdown')
        print('Normal DB shutdown verified; syncing and sealing read-only fixture', flush=True)
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
                    'completion': 'fresh positive ordered log markers; pinned completion write precedes success log; clean exit0 or configured SIGINT exit130; All DBs closed and Nethermind is shut down; noOOM/error',
                    'retention': 'user-authorized persistent fixture; manual retirement only; do not auto-delete at deadline'}
        if recovering:
            manifest['recovery'] = recovery
            manifest['completion'] = 'pinned old fresh positive completion and clean shutdown; new baseline no-migration startup, exact100case oracle and clean shutdown'
        manifest['upper_allocated_bytes'] = int(run('du', '-sx', '-B1', root / 'upper', timeout=300).split()[0])
        monitoring.set()
        watcher.join(timeout=30)
        pressure()
        shutil.copytree(output, root / ('recovery-evidence-' + os.environ['GITHUB_RUN_ID'] if recovering else 'evidence'))
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
                found = subprocess.run(['docker', 'inspect', container], capture_output=True, timeout=30)
                if found.returncode == 0:
                    if json.loads(found.stdout)[0]['State']['Running']:
                        raise RuntimeError('owned container still running; manual bounded cleanup required') from error
                    run('docker', 'rm', container)
                elif 'No such' not in found.stderr.decode(errors='replace'):
                    raise RuntimeError('cannot establish owned container stopped') from error
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
    finish = dt.datetime.fromisoformat(args.finish.replace('Z', '+00:00')).timestamp()
    if time.time() + 1800 > finish:
        raise RuntimeError('insufficient untimed compatibility/cleanup budget')
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
    atomic(child / 'owner.json', {'run': os.environ['GITHUB_RUN_ID'], 'container': container, 'fixture': str(args.root), 'manifest_sha256': args.manifest_sha256})
    running = False
    overlay(args.root / 'merged', child)
    samples = []
    def pressure():
        samples.append({'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'scratch': space(PARENT), 'root': space(Path('/'))})
        atomic(output / 'disk.json', samples)
        if time.time() > finish - 1200:
            raise RuntimeError('compatibility deadline reached; reserving cleanup time')
    pressure()
    monitoring, watcher = start_watchdog(pressure, output)
    try:
        space(PARENT)
        space(Path('/'))
        run('docker', 'pull', args.image, timeout=600)
        running = True
        run('docker', 'run', '-d', '--name', container, '--restart', 'no', '--stop-signal', 'SIGINT',
            '-p', '127.0.0.1:18545:8545', '-v', f'{child}/merged:/execution-data:rw', args.image, *FLAGS)
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
        monitoring.set()
        watcher.join(timeout=30)
        if running:
            stop(container, output)
        run('umount', child / 'merged', root=True)
        # Owned child only, after successful unmount; never follow a lazy/forced unmount with deletion.
        run('rm', '-rf', '--', child, root=True)


def gate(args):
    manifest = verify(args.root, args.manifest_sha256)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    observed = inspect(args.container)
    atomic(output / 'prepared-container.json', observed)
    if observed['Config']['Image'] not in manifest['allowed_images'] or observed['Config']['Image'] != args.image or observed['Config']['Cmd'] != FLAGS:
        raise RuntimeError('prepared arm image/effective command mismatch')
    text = logs(args.container)
    check_logs(text, reused=True)
    if args.action == 'stopped':
        state = observed['State']
        normal_shutdown(observed, text)
        atomic(output / 'prepared-stopped.json', {'manifest_sha256': args.manifest_sha256, 'image': args.image, 'state': state})
        return
    if not observed['State']['Running']:
        raise RuntimeError('prepared arm is not running')
    child = Path(args.child_source).resolve()
    if child == args.root / 'merged':
        raise RuntimeError('arm must use a fresh writable child, never the sealed mount directly')
    mount = mounted(child, False)
    if f'lowerdir={args.root}/merged' not in mount['options'].split(','):
        raise RuntimeError('arm child does not use the verified sealed lower')
    binds = [item for item in observed['Mounts'] if item['Destination'] == '/execution-data']
    if len(binds) != 1 or binds[0]['Source'] != str(child) or not binds[0]['RW']:
        raise RuntimeError('arm data bind is not the expected writable child')
    if any(item.get('RW') and (item['Source'] == str(SOURCE) or item['Source'].startswith(str(args.root) + '/')) for item in observed['Mounts']):
        raise RuntimeError('raw source/prepared generation was bound writable')
    current = identity()
    if args.action == 'gate':
        if digest(Path(args.oracle).read_bytes()) != manifest['oracle_sha256']:
            raise RuntimeError('oracle pin mismatch')
        validate_responses(json.loads(Path(args.oracle).read_text()), output / 'responses.json')
        check_logs(logs(args.container), reused=True)
    atomic(output / 'prepared-running.json', {'manifest_sha256': args.manifest_sha256, 'image': args.image, 'identity': current,
                                             'qualified_cases': 100 if args.action == 'gate' else None, 'phase': args.action})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['prepare', 'recover', 'verify', 'qualify', 'gate', 'running', 'stopped'])
    parser.add_argument('--root', type=Path)
    parser.add_argument('--manifest-sha256')
    parser.add_argument('--image', default=BASELINE)
    parser.add_argument('--container')
    parser.add_argument('--child-source')
    parser.add_argument('--rpc-port', type=int, default=18545)
    parser.add_argument('--finish', default='2026-10-30T00:06:04Z')
    parser.add_argument('--recovery-evidence', type=Path)
    parser.add_argument('--output', default='prepared-results')
    parser.add_argument('--oracle', default=str(Path(__file__).with_name('prepared-snapshot-oracle.json')))
    args = parser.parse_args()
    global RPC_PORT
    RPC_PORT = args.rpc_port
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(RuntimeError('termination requested')))
    if args.action in ('prepare', 'recover'):
        prepare(args)
    elif args.action == 'qualify':
        qualify(args)
    elif args.action in ('gate', 'running', 'stopped'):
        gate(args)
    else:
        print(json.dumps(verify(args.root, args.manifest_sha256), indent=2))


if __name__ == '__main__':
    main()
