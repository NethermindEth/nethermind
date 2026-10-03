#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Bounded real-CL wrapper load; preparation, arrivals, drain and finality are separate."""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
import math
import shlex
import subprocess
import time
import urllib.request
import urllib.error
from pathlib import Path
from drive import Rpc, RpcError, quantity

MAX_JSON = 24 * 1024 * 1024
PROOF_ERROR = 'a wrapper dependency proof failed verification'


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def read_json(path):
    with path.open('rb') as source:
        content = source.read(MAX_JSON + 1)
    if len(content) > MAX_JSON:
        raise ValueError('JSON exceeds load-driver bound')
    return json.loads(content)


def basename(value):
    if not isinstance(value, str) or Path(value).name != value or not value.endswith('.json'):
        raise ValueError('Manifest request must be a JSON basename')
    return value


def beacon(url, path, timeout=5):
    with urllib.request.urlopen(url + path, timeout=timeout) as response:
        content = response.read(MAX_JSON + 1)
    if len(content) > MAX_JSON:
        raise RuntimeError('Beacon response exceeds bound')
    return json.loads(content)


def dependencies(block):
    declarations = []
    for transaction in block['transactions']:
        for frame in transaction.get('frames', []):
            if quantity(frame['mode']) != 4:
                continue
            data = bytes.fromhex(frame['data'].removeprefix('0x'))
            if len(data) % 96:
                raise RuntimeError('Included dependency frame is not canonical')
            for offset in range(0, len(data), 96):
                item = data[offset:offset + 96]
                if any(item[:31]) or item[31] not in (0x10, 0x11):
                    raise RuntimeError('Included dependency declaration is invalid')
                declarations.append(item.hex())
    unique = sorted(set(declarations))
    return {'rawDeclarations': len(declarations), 'canonicalDependencies': len(unique),
            'signatures': sum(int(item[62:64], 16) == 0x10 for item in unique),
            'genericStarks': sum(int(item[62:64], 16) == 0x11 for item in unique),
            'canonicalTriplesSha256': hashlib.sha256(bytes.fromhex(''.join(unique))).hexdigest()}


def resource_guard(path):
    if path is None:
        return None
    value = read_json(path)
    if value.get('error') or any(len(value[field]) != 2 for field in ('elMemoryPercent', 'elRestarts', 'elOomKilled')):
        raise RuntimeError('Resource monitor metrics unavailable')
    numbers = [value['hostAvailableBytes'], value['hostCpuPercent'], *value['elMemoryPercent']]
    if not all(isinstance(number, (int, float)) and math.isfinite(number) for number in numbers):
        raise RuntimeError('Resource monitor metrics are invalid')
    captured = datetime.datetime.fromisoformat(value['capturedUtc'].replace('Z', '+00:00')).timestamp()
    if not 0 <= time.time() - captured <= 30:
        raise RuntimeError('Resource monitor is stale')
    if value['hostAvailableBytes'] < 6 * 1024 ** 3 or value['hostCpuPercent'] >= 85:
        raise RuntimeError('Host resource stop threshold')
    if any(percent >= 80 for percent in value['elMemoryPercent']) or any(value['elRestarts']) or any(value['elOomKilled']):
        raise RuntimeError('Execution-node resource stop threshold')
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--rpc1', default='http://127.0.0.1:19445')
    parser.add_argument('--rpc2', default='http://127.0.0.1:19545')
    parser.add_argument('--beacon1', default='http://127.0.0.1:19552')
    parser.add_argument('--beacon2', default='http://127.0.0.1:19652')
    parser.add_argument('--rate', type=float, default=.1)
    parser.add_argument('--limit', type=int, default=8)
    parser.add_argument('--workers', type=int, default=2)
    parser.add_argument('--invalid-only', action='store_true')
    parser.add_argument('--observe-report', type=Path, help='Observe already admitted transactions without resubmitting')
    parser.add_argument('--allow-shared-dependency', action='store_true')
    parser.add_argument('--invalid-repeat', type=int, default=1)
    parser.add_argument('--drain-timeout', type=float, default=180)
    parser.add_argument('--finality-timeout', type=float, default=1200)
    parser.add_argument('--health-file', type=Path)
    parser.add_argument('--proof-metadata', type=Path)
    parser.add_argument('--dependency-inspector', help='LeanBench command for the managed body/commitment inspector')
    parser.add_argument('--source-revision', required=True)
    args = parser.parse_args()
    if not all(math.isfinite(value) for value in (args.rate, args.drain_timeout, args.finality_timeout)) \
            or not 0 < args.rate <= 8 or not 0 < args.drain_timeout <= 300 or not 0 <= args.finality_timeout <= 1800:
        parser.error('Invalid finite rate/deadlines')
    if not 1 <= args.limit <= 64 or not 1 <= args.workers <= (8 if args.invalid_only else 2) \
            or not 1 <= args.invalid_repeat <= 8 or (not args.invalid_only and (args.rate > 2 or args.invalid_repeat != 1)):
        parser.error('Invalid bounded workload size/concurrency')
    if args.proof_metadata and not args.invalid_only and not args.dependency_inspector:
        parser.error('Proof metadata requires the managed dependency inspector')
    if args.observe_report and args.out.resolve() == args.observe_report.parent.resolve():
        parser.error('Recovery must preserve the failed report in a separate output directory')
    manifest = read_json(args.manifest)
    entries = manifest['requests'][:args.limit]
    if not entries or len(entries) != args.limit or len(entries) * args.invalid_repeat > 64:
        parser.error('Manifest/invalid burst exceeds bounded request count')
    if (len(entries) * args.invalid_repeat - 1) / args.rate > 180:
        parser.error('Arrival plan exceeds 180-second window')
    hashes = [entry['transactionHash'].lower() for entry in entries]
    if len(set(hashes)) != len(hashes) or [entry['nonce'] for entry in entries] != list(range(manifest['firstNonce'], manifest['firstNonce'] + len(entries))):
        parser.error('Manifest must contain unique consecutive transactions')
    declared = [tuple(dep[key] for key in ('scheme', 'dataHash', 'verificationKey')) for entry in entries for dep in entry['dependencies']]
    if any(int(item[0], 16) != 0x10 for item in declared):
        parser.error('This bounded spam plan permits SPHINCS dependencies only')
    if args.allow_shared_dependency:
        if not manifest.get('sharedDependency') or len(set(declared)) != 1:
            parser.error('Shared load requires an explicitly labelled single-dependency manifest')
    elif manifest.get('sharedDependency') or len(set(declared)) != len(declared):
        parser.error('Fresh load requires distinct dependencies; shared load must be explicit')
    for entry in entries:
        basename(entry['requestFile'])
        basename(entry['negativeRequestFile'])
    urls, beacons = (args.rpc1, args.rpc2), (args.beacon1, args.beacon2)
    args.out.mkdir(parents=True, exist_ok=True)
    report = {'startedUtc': utc(), 'sourceRevision': args.source_revision,
              'driverSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              'manifestSha256': hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              'fixtureOffset': manifest.get('fixtureOffset'), 'preparation': 'excluded; signed wrappers prepared before driver start',
              'requestedRate': args.rate, 'invalidOnly': args.invalid_only, 'workers': args.workers,
              'uniqueTransactions': len(entries), 'uniqueSignatureDependencies': len(set(declared)), 'sharedDependency': args.allow_shared_dependency,
              'measurementScope': 'invalid-wrapper rejection/backpressure only' if args.invalid_only else 'shared-dependency transaction transport/pool/EVM only' if args.allow_shared_dependency else 'fresh SPHINCS dependencies',
              'negativeTemplateCount': len(entries), 'plannedNegativeAttempts': len(entries) * args.invalid_repeat,
              'negativeCacheNotice': 'Repeated templates may use cached rejections; RPC outcomes are not native verification counts',
              'completed': False, 'inclusionVerified': False, 'completedFinality': False,
              'admissions': [], 'blocks': [], 'health': [], 'stopEvents': [],
              'observerTransportRetries': 0, 'observerBeaconRetries': 0, 'readDiagnostics': [],
              'proofBytes': 'not exposed by eth_getBlock; dependency counts are decoded from included frame bodies'}

    def observer_read(kind, index, target, fetch):
        deadline = time.monotonic() + (8 if kind == 'beacon' else 5)
        attempts = 0
        while True:
            resource_guard(args.health_file)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise RuntimeError(f'{kind} observer node{index + 1} {target}: read deadline exceeded')
            attempt_start = time.monotonic()
            diagnostic = {'kind': kind, 'node': index + 1, 'target': target, 'attempt': attempts + 1}
            try:
                return fetch(min(2, remaining))
            except urllib.error.HTTPError:
                # HTTP 404 during beacon propagation is handled by the receipt observer.
                raise
            except (ConnectionError, TimeoutError, urllib.error.URLError) as error:
                diagnostic['error'] = str(error)[:256]
                resource_guard(args.health_file)
                attempts += 1
                report['observerTransportRetries'] += 1
                if kind == 'beacon':
                    report['observerBeaconRetries'] += 1
                if attempts >= 3 or time.monotonic() >= deadline:
                    raise RuntimeError(f'{kind} observer node{index + 1} {target}: {error}') from error
                time.sleep(.25)
            finally:
                diagnostic['latencySeconds'] = time.monotonic() - attempt_start
                report['readDiagnostics'].append(diagnostic)
                report['readDiagnostics'] = report['readDiagnostics'][-128:]

    def rpc(index, method, params=()):
        if method == 'eth_sendProofWrapper':
            return Rpc(urls[index], timeout=10).call(method, list(params))
        return observer_read('rpc', index, method, lambda timeout: Rpc(urls[index], timeout=timeout).call(method, list(params)))

    def read_beacon(index, path):
        return observer_read('beacon', index, path, lambda timeout: beacon(beacons[index], path, timeout=timeout))

    def execution_bid(index, slot):
        value = read_beacon(index, f'/eth/v2/beacon/blocks/{slot}')
        if value.get('execution_optimistic', False):
            raise RuntimeError('Beacon execution bid is optimistic')
        return value['data']['message']['body']['signed_execution_payload_bid']['message']

    def save():
        temporary = args.out / 'report.tmp'
        temporary.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
        temporary.replace(args.out / 'report.json')

    def record_stop(reason, phase):
        text = str(reason)[:512]
        report.setdefault('stopReason', text)
        report['stopEvents'].append({'utc': utc(), 'phase': phase, 'reason': text})
        report['stopEvents'] = report['stopEvents'][-16:]

    def health():
        resource = resource_guard(args.health_file)
        nodes, cls = [], []
        for index in range(2):
            head = rpc(index, 'eth_getBlockByNumber', ['latest', False])
            pool = rpc(index, 'txpool_status')
            peers = rpc(index, 'admin_peers')
            sync = read_beacon(index, '/eth/v1/node/syncing')['data']
            finality = read_beacon(index, '/eth/v1/beacon/states/head/finality_checkpoints')['data']['finalized']
            if not any('lean/1' in peer.get('caps', []) for peer in peers):
                raise RuntimeError('Lean peer lost')
            if quantity(pool['pending']) + quantity(pool['queued']) > 32 or time.time() - quantity(head['timestamp']) >= 36:
                raise RuntimeError('Pool/head-lag stop threshold')
            if sync['is_optimistic'] or sync.get('el_offline') or quantity(sync['sync_distance']) > 2 \
                    or quantity(sync['head_slot']) // 32 - quantity(finality['epoch']) > 4:
                raise RuntimeError('Consensus-health stop threshold')
            nodes.append({'number': head['number'], 'hash': head['hash'], 'pool': pool})
            cls.append({'headSlot': sync['head_slot'], 'finalized': finality})
        if abs(quantity(nodes[0]['number']) - quantity(nodes[1]['number'])) > 2:
            raise RuntimeError('Execution heads diverged')
        result = {'utc': utc(), 'nodes': nodes, 'consensus': cls, 'resource': resource}
        report['health'].append(result)
        report['health'] = report['health'][-180:]
        return result

    started = time.monotonic()

    def submit(entry, index, sequence):
        result = {'sequence': sequence, 'name': entry['name'], 'mode': entry['mode'], 'nonce': entry['nonce'],
                  'transactionHash': entry['transactionHash'], 'ingressNode': index + 1,
                  'offeredSeconds': time.monotonic() - started, 'accepted': False, 'validSubmissionOffered': False, 'negativeSubmissionOffered': False}
        try:
            negative = read_json(args.manifest.parent / entry['negativeRequestFile'])
            if negative.get('method') != 'eth_sendProofWrapper':
                raise RuntimeError('Manifest must use proof-wrapper ingress')
            known_before = rpc(index, 'eth_getTransactionByHash', [entry['transactionHash']]) if args.invalid_only else None
            result['preexistingTransaction'] = known_before is not None
            try:
                result['negativeSubmissionOffered'] = True
                result['negativeOfferedSeconds'] = time.monotonic() - started
                rpc(index, negative['method'], negative['params'])
            except RpcError as error:
                proof_rejected = error.error.get('code') == -32000 and error.error.get('message') == PROOF_ERROR
                bounded_busy = error.error.get('code') == -32000 and error.error.get('message') == 'Proof admission is busy; retry later.'
                result['negative'] = {'code': error.error.get('code'), 'message': error.error.get('message'),
                                      'passed': proof_rejected or (args.invalid_only and bounded_busy),
                                      'outcome': 'proofRejected' if proof_rejected else 'boundedBusy' if bounded_busy else 'unexpected'}
                if bounded_busy:
                    result['deferredByBackpressure'] = True
                    if not args.invalid_only:
                        raise RuntimeError('Bounded admission backpressure deferred the negative probe; valid wrapper not offered')
                if not result['negative']['passed']:
                    raise RuntimeError('Unexpected invalid-wrapper response')
            else:
                raise RuntimeError('Invalid wrapper was accepted')
            known_after = rpc(index, 'eth_getTransactionByHash', [entry['transactionHash']])
            if known_before is None and known_after is not None:
                raise RuntimeError('Invalid wrapper leaked into pool')
            if known_before is not None and (known_after is None or known_after.get('hash', '').lower() != entry['transactionHash'].lower()):
                raise RuntimeError('Previously known transaction changed during invalid ingress')
            if not args.invalid_only:
                request = read_json(args.manifest.parent / entry['requestFile'])
                if request.get('method') != 'eth_sendProofWrapper':
                    raise RuntimeError('Manifest must use proof-wrapper ingress')
                result['validSubmissionOffered'] = True
                result['validOfferedSeconds'] = time.monotonic() - started
                accepted = rpc(index, request['method'], request['params'])
                if entry['transactionHash'].lower() not in [item.lower() for item in accepted]:
                    raise RuntimeError('Valid wrapper did not admit expected transaction')
                result.update(accepted=True, acceptedSeconds=time.monotonic() - started)
        except Exception as error:
            result['error'] = str(error)[:512]
        result['finishedSeconds'] = time.monotonic() - started
        return result

    by_hash = {entry['transactionHash'].lower(): entry for entry in entries}
    observed, blocks, futures = {}, {}, {}

    def observe():
        for admission in report['admissions']:
            key = admission['transactionHash']
            if not admission['accepted'] or key in observed:
                continue
            other = 1 - (admission['ingressNode'] - 1)
            known = rpc(other, 'eth_getTransactionByHash', [key])
            if known is not None and 'gossipObservedSeconds' not in admission and not args.observe_report:
                admission.update(gossipObservedSeconds=time.monotonic() - started,
                                 gossipSeenBeforeReceipt=known.get('blockHash') is None)
            receipts = [rpc(index, 'eth_getTransactionReceipt', [key]) for index in range(2)]
            if not all(receipts):
                continue
            if any(receipt['status'] != '0x1' for receipt in receipts) or receipts[0]['blockHash'] != receipts[1]['blockHash']:
                raise RuntimeError('Receipt status/hash mismatch between execution nodes')
            block_hash = receipts[0]['blockHash']
            if args.observe_report:
                admission.setdefault('recoveryReceiptObservedSeconds', time.monotonic() - started)
            else:
                admission.setdefault('includedSeconds', time.monotonic() - started)
            admission['receipts'] = [{field: receipt[field] for field in ('status', 'blockHash', 'blockNumber', 'gasUsed')} for receipt in receipts]
            if block_hash not in blocks:
                pair = [rpc(index, 'eth_getBlockByHash', [block_hash, True]) for index in range(2)]
                if not all(block and block['hash'] == block_hash for block in pair) or pair[0]['transactionsRoot'] != pair[1]['transactionsRoot']:
                    raise RuntimeError('Imported block mismatch')
                block = pair[0]
                counts = dependencies(block)
                expected = {int(dep['scheme'], 16).to_bytes(32, 'big').hex() + dep['dataHash'].removeprefix('0x').lower() + dep['verificationKey'].removeprefix('0x').lower()
                            for tx in block['transactions'] for dep in by_hash.get(tx['hash'].lower(), {}).get('dependencies', [])}
                expected_hash = hashlib.sha256(bytes.fromhex(''.join(sorted(expected)))).hexdigest()
                if expected_hash != counts['canonicalTriplesSha256']:
                    raise RuntimeError('Included dependency union differs from signed manifest')
                if counts['genericStarks'] > 1:
                    raise RuntimeError('User generic-proof-per-block scope exceeded')
                slot = quantity(block['slotNumber'])
                try:
                    bids = [execution_bid(index, slot) for index in range(2)]
                except urllib.error.HTTPError as error:
                    if error.code == 404:
                        continue
                    raise
                if any(bid['block_hash'] != block_hash for bid in bids):
                    raise RuntimeError('Beacon execution bid does not anchor the receipt block')
                try:
                    proof = read_json(args.proof_metadata).get('proofs', {}).get(block_hash) if args.proof_metadata else None
                except FileNotFoundError:
                    proof = None
                if args.proof_metadata and proof is None:
                    continue
                inspection = None
                if args.dependency_inspector:
                    path = args.out / f'block-{len(blocks):03}.json'
                    path.write_text(json.dumps(block))
                    output = path.with_suffix('.inspection.json')
                    command = shlex.split(args.dependency_inspector) + ['--devnet-block=' + str(path), '--out=' + str(output)]
                    if proof is not None:
                        command.append('--expected-commitment=' + proof['dependencyHash'])
                    subprocess.run(command, check=True, timeout=15)
                    inspection = read_json(output)
                    if inspection['blockHash'] != block_hash or any(inspection[field] != counts[field] for field in ('rawDeclarations', 'canonicalDependencies', 'signatures', 'genericStarks')):
                        raise RuntimeError('Managed inspector and observed frame counts differ')
                value = {'blockHash': block_hash, 'number': block['number'], 'slot': slot, 'gasUsed': block['gasUsed'],
                         'gasLimit': block['gasLimit'], 'transactionCount': len(block['transactions']),
                         'aggregation': counts, 'bodyDependencyUnionMatchesManifest': True,
                         'proof': proof, 'managedInspection': inspection, 'beaconAnchoredBoth': True, 'finalizedBoth': False}
                blocks[block_hash] = value
                report['blocks'].append(value)
            observed[key] = block_hash

    try:
        if any(quantity(rpc(index, 'eth_chainId')) != manifest['chainId'] for index in range(2)):
            raise RuntimeError('Chain IDs do not match manifest')
        report['nodeVersions'] = [rpc(index, 'web3_clientVersion') for index in range(2)]
        report['initialSenderNonces'] = [quantity(rpc(index, 'eth_getTransactionCount', [manifest['sender'], 'latest'])) for index in range(2)]
        if args.observe_report:
            previous = read_json(args.observe_report)
            admissions = [entry for entry in previous.get('admissions', []) if entry.get('accepted')]
            if not admissions or any(entry['transactionHash'].lower() not in by_hash for entry in admissions):
                raise RuntimeError('Recovery report does not match the signed manifest')
            report['recoveryFrom'] = {'sha256': hashlib.sha256(args.observe_report.read_bytes()).hexdigest(), 'startedUtc': previous.get('startedUtc'), 'priorError': previous.get('error')}
            report['admissions'] = admissions
        elif not args.invalid_only:
            if report['initialSenderNonces'] != [manifest['firstNonce']] * 2:
                raise RuntimeError('Sender nonce changed; regenerate unique nonce manifest')
        health()
        started = time.monotonic()
        report['arrivalStartedUtc'] = utc()
        jobs = [] if args.observe_report else [entry for _ in range(args.invalid_repeat) for entry in entries]
        offered, stop_admissions = 0, False
        arrival_end = None
        next_health = 0
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
            while offered < len(jobs) or futures or (not args.invalid_only and len(observed) < sum(item['accepted'] for item in report['admissions'])):
                now = time.monotonic() - started
                if now >= next_health:
                    try:
                        health()
                    except Exception as error:
                        record_stop(error, 'health')
                        stop_admissions = True
                    next_health = now + 2
                while not stop_admissions and offered < len(jobs) and len(futures) < args.workers and now >= offered / args.rate:
                    entry = jobs[offered]
                    future = executor.submit(submit, entry, offered % 2, offered)
                    futures[future] = entry
                    offered += 1
                for future in list(futures):
                    if future.done():
                        item = future.result()
                        report['admissions'].append(item)
                        del futures[future]
                        if item.get('error'):
                            record_stop(item['error'], 'admission')
                            stop_admissions = True
                if (offered == len(jobs) or stop_admissions) and arrival_end is None:
                    arrival_end = time.monotonic() - started
                    report['arrivalWindowSeconds'] = arrival_end
                if not args.invalid_only:
                    observe()
                save()
                if arrival_end is not None and time.monotonic() - started - arrival_end >= args.drain_timeout:
                    raise RuntimeError('Receipt drain deadline exceeded')
                if stop_admissions and not futures and len(observed) == sum(item['accepted'] for item in report['admissions']):
                    break
                time.sleep(.25)
        report['timedRunSeconds'] = time.monotonic() - started
        report['acceptedTransactions'] = sum(item['accepted'] for item in report['admissions'])
        report['invalidOutcomes'] = {outcome: sum(item.get('negative', {}).get('outcome') == outcome for item in report['admissions']) for outcome in ('proofRejected', 'boundedBusy', 'unexpected')}
        report['includedTransactions'] = len(observed)
        report['goodputIncludingDrainTxPerSecond'] = len(observed) / report['timedRunSeconds']
        times = [] if args.observe_report else sorted(item['negativeOfferedSeconds'] if args.invalid_only else item['validOfferedSeconds'] for item in report['admissions'] if item.get('negativeSubmissionOffered' if args.invalid_only else 'validSubmissionOffered'))
        report['interarrivalMetricScope'] = 'negative-wrapper RPC starts' if args.invalid_only else 'valid-wrapper RPC starts'
        report['observedInterarrivalOffersPerSecond'] = (len(times) - 1) / (times[-1] - times[0]) if len(times) > 1 and times[-1] > times[0] else None
        report['offeredAttemptsOverTimedWindowPerSecond'] = len(times) / report['timedRunSeconds']
        report['arrivalWindowGoodputTxPerSecond'] = sum(item.get('includedSeconds', math.inf) <= report['arrivalWindowSeconds'] for item in report['admissions']) / max(report['arrivalWindowSeconds'], .001)
        if args.observe_report:
            for key in ('goodputIncludingDrainTxPerSecond', 'observedInterarrivalOffersPerSecond', 'offeredAttemptsOverTimedWindowPerSecond', 'arrivalWindowGoodputTxPerSecond'):
                report[key] = None
            report['measurementScope'] = 'recovery observation only; no new arrivals or throughput measurement'
        report['receiptDrainSenderNonces'] = [quantity(rpc(index, 'eth_getTransactionCount', [manifest['sender'], 'latest'])) for index in range(2)]
        expected_nonce = min(report['initialSenderNonces']) if args.invalid_only else manifest['firstNonce'] + report['includedTransactions']
        if args.invalid_only:
            if report['receiptDrainSenderNonces'] != report['initialSenderNonces']:
                raise RuntimeError('Invalid-only ingress changed sender nonces')
            report['invalidIngressSenderNoncesUnchanged'] = True
        elif args.observe_report:
            if any(nonce < expected_nonce for nonce in report['receiptDrainSenderNonces']):
                raise RuntimeError('Recovery sender nonce regressed below included count')
        elif report['receiptDrainSenderNonces'] != [expected_nonce] * 2:
            raise RuntimeError('Sender nonces differ from included count at receipt drain')
        report['receiptDrainCompletedUtc'] = utc()
        report['inclusionVerified'] = report['acceptedTransactions'] > 0 and len(observed) == report['acceptedTransactions']
        save()
        deadline = time.monotonic() + args.finality_timeout
        while report['blocks'] and time.monotonic() < deadline:
            resource_guard(args.health_file)
            checkpoints = [read_beacon(index, '/eth/v1/beacon/states/head/finality_checkpoints')['data']['finalized'] for index in range(2)]
            for block in report['blocks']:
                if all(quantity(checkpoint['epoch']) * 32 > block['slot'] for checkpoint in checkpoints):
                    for index in range(2):
                        canonical = rpc(index, 'eth_getBlockByNumber', [block['number'], False])
                        if canonical is None or canonical['hash'] != block['blockHash']:
                            raise RuntimeError('Finalized receipt block is no longer canonical')
                    bids = [execution_bid(index, block['slot']) for index in range(2)]
                    if any(bid['block_hash'] != block['blockHash'] for bid in bids):
                        raise RuntimeError('Finalized canonical beacon bid no longer anchors the receipt block')
                    block['finalityBeaconRecheckedBoth'] = True
                    block['finalizedBoth'] = True
            report['finalityCheckpoints'] = checkpoints
            save()
            if all(block['finalizedBoth'] for block in report['blocks']):
                break
            time.sleep(5)
        report['completedFinality'] = bool(report['blocks']) and all(block['finalizedBoth'] for block in report['blocks'])
        report['finalSenderNonces'] = [quantity(rpc(index, 'eth_getTransactionCount', [manifest['sender'], 'latest'])) for index in range(2)]
        if any(nonce < expected_nonce for nonce in report['finalSenderNonces']):
            raise RuntimeError('Final sender nonce regressed below the receipt drain')
        report['completed'] = not report.get('stopReason') and offered == len(jobs) and len(observed) == report['acceptedTransactions']
        if report['blocks'] and not all(block['finalizedBoth'] for block in report['blocks']):
            report['completed'] = False
            report['error'] = 'Finality observation deadline exceeded'
    except Exception as error:
        report['error'] = str(error)[:512]
        record_stop(error, 'observation')
        raise
    finally:
        # The executor joins in-flight admissions even on observer failure; keep those outcomes.
        for future in futures:
            if future.done():
                report['admissions'].append(future.result())
        report['acceptedTransactions'] = sum(item['accepted'] for item in report['admissions'])
        report['includedTransactions'] = len(observed)
        report['validTransactionsOffered'] = 0 if args.observe_report else sum(bool(item.get('validSubmissionOffered')) for item in report['admissions'])
        report['negativeAttemptCount'] = 0 if args.observe_report else sum(bool(item.get('negativeSubmissionOffered')) for item in report['admissions'])
        report['finishedUtc'] = utc()
        report['totalIncludingFinalitySeconds'] = time.monotonic() - started
        save()
    print(json.dumps({key: report.get(key) for key in ('completed', 'acceptedTransactions', 'includedTransactions', 'timedRunSeconds', 'stopReason', 'error')}))
    if not report['completed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
