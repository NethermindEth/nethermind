#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import argparse
import hashlib
import json
import math
import pathlib
import re
import struct
import subprocess
import sys
import time
import urllib.error

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from drive import Rpc, RpcError, quantity, wait_for
from spam import beacon, dependencies, read_json, basename, utc

HASH = re.compile(r'0x[0-9a-fA-F]{64}')


class ObservationMoved(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def submit_wrapper(node, request, admission):
    admission['admissionUncertain'] = True
    try:
        result = node.call(request['method'], request['params'])
    except RpcError:
        admission['admissionUncertain'] = False
        raise
    require(result == [admission['transactionHash']], 'Wrapper response did not confirm the signed transaction hash')
    admission.update(accepted=True, admissionUncertain=False, acceptedUtc=utc())


def transient_read(error):
    if isinstance(error, urllib.error.HTTPError):
        return error.code in (404, 408, 429, 502, 503, 504)
    if isinstance(error, urllib.error.URLError):
        return True
    if isinstance(error, (TimeoutError, ConnectionError)):
        return True
    return error.__cause__ is not None and transient_read(error.__cause__)


def retry_read(action, record_retry=lambda error: None):
    for attempt in range(3):
        try:
            return action()
        except Exception as error:
            if not transient_read(error) or attempt == 2:
                raise
            record_retry(error)
            time.sleep(.25)


def ensure_canonical(nodes, block, record_retry=lambda error: None):
    for node in nodes:
        current = retry_read(lambda: node.call('eth_getBlockByNumber', [block['number'], False]), record_retry)
        if current is None or current['hash'] != block['hash']:
            raise ObservationMoved('Receipt block is no longer canonical on both nodes')


def canonical_receipts(nodes, admissions, record_retry=lambda error: None):
    result = []
    for admission in admissions:
        pair = [retry_read(lambda: node.call('eth_getTransactionReceipt', [admission['transactionHash']]), record_retry) for node in nodes]
        if not all(pair):
            raise ObservationMoved(('Uncertain admission has no receipt on both nodes: ' if admission.get('admissionUncertain') else 'Waiting for both current receipts: ') + admission['transactionHash'])
        require(all(r['transactionHash'] == admission['transactionHash'] and r['status'] == '0x1' for r in pair), 'Both receipts must identify the signed transaction and have status 0x1')
        if pair[0]['blockHash'] != pair[1]['blockHash'] or pair[0]['blockNumber'] != pair[1]['blockNumber']:
            raise ObservationMoved('Receipt placement differs between nodes')
        ensure_canonical(nodes, {'number': pair[0]['blockNumber'], 'hash': pair[0]['blockHash']}, record_retry)
        result.append(pair)
    return result


def validate_health(v):
    require(not v.get('error') and math.isfinite(v['capturedEpoch']) and 0 <= time.time() - v['capturedEpoch'] <= 15, 'Health sample is unavailable, nonfinite or stale')
    require(len(v['elMemoryBytes']) == len(v['elMemoryLimits']) == 2, 'Health sample must cover two execution nodes')
    require(all(isinstance(n, (float, int)) and math.isfinite(n) and n >= 0 for n in [v['hostAvailableBytes'], v['hostAvailableFloorBytes'], v['hostCpuPercent'], *v['elMemoryBytes'], *v['elMemoryLimits']]), 'Resource values must be finite and nonnegative')
    require(all(n > 0 for n in v['elMemoryLimits']), 'Execution memory limits must be positive')
    require(v['hostAvailableFloorBytes'] >= 6 * 1024**3, 'Host memory floor must be at least 6 GiB')
    require(v['hostAvailableBytes'] >= v['hostAvailableFloorBytes'], 'Host available memory is below the safety floor')
    require(v['hostCpuPercent'] < 85, 'Host CPU is above the safety threshold')
    require(all(x < y * .8 for x, y in zip(v['elMemoryBytes'], v['elMemoryLimits'])), 'Execution memory is above the safety threshold')
    require(v['elOomKilled'] == [False, False] and v['elRestarts'] == [0, 0], 'Execution node OOM or restart detected')


def ensure_el_finalized(nodes, block, record_retry=lambda error: None):
    for node in nodes:
        finalized = retry_read(lambda: node.call('eth_getBlockByNumber', ['finalized', False]), record_retry)
        if finalized is None:
            raise ObservationMoved('Waiting for execution finalized block')
        require(isinstance(finalized, dict) and isinstance(finalized.get('number'), str)
                and isinstance(finalized.get('hash'), str) and HASH.fullmatch(finalized['hash']),
                'Malformed execution finalized block')
        try:
            height = quantity(finalized['number'])
        except (ValueError, TypeError) as error:
            raise RuntimeError('Malformed execution finalized block') from error
        require(finalized['number'].startswith('0x') and height >= 0, 'Malformed execution finalized block')
        if height < quantity(block['number']):
            raise ObservationMoved('Execution finalized block has not reached the anchored block')
        ensure_canonical([node], finalized, record_retry)
        ensure_canonical([node], block, record_retry)


def main():
    p = argparse.ArgumentParser(description='Bounded real-CL mixed-proof validation; no Engine driving or submission retries.')
    p.add_argument('--observe-report', type=pathlib.Path, help='Observe accepted or uncertain signed transactions into a separate report; never submit')
    p.add_argument('--manifest', action='append', required=True, type=pathlib.Path)
    p.add_argument('--out', required=True, type=pathlib.Path)
    p.add_argument('--relay', required=True)
    p.add_argument('--dotnet', default='dotnet')
    p.add_argument('--helper', required=True)
    p.add_argument('--source', required=True)
    p.add_argument('--mode', choices=['reuse', 'merge'], required=True)
    p.add_argument('--finality-timeout', type=int, default=1200)
    p.add_argument('--health-file', required=True, type=pathlib.Path)
    p.add_argument('--rpc-port1', type=int, default=19445)
    p.add_argument('--rpc-port2', type=int, default=19545)
    p.add_argument('--beacon-port1', type=int, default=19552)
    p.add_argument('--beacon-port2', type=int, default=19652)
    a = p.parse_args()

    require(0 <= a.finality_timeout <= 1800, 'Finality timeout must be between 0 and 1800 seconds')
    require(all(1 <= port <= 65535 for port in (a.rpc_port1, a.rpc_port2, a.beacon_port1, a.beacon_port2)), 'RPC and beacon ports must be between 1 and 65535')
    require(len(a.manifest) == (1 if a.mode == 'reuse' else 2), 'Manifest count must match reuse or merge mode')
    manifests = [read_json(path) for path in a.manifest]
    require(all(len(m['requests']) == 1 and m['nativeProfile']['abi'] == 5 for m in manifests), 'Each manifest must contain one ABI5 request')
    entries = [m['requests'][0] for m in manifests]
    require(all(e['mode'] == 'recursive' for e in entries), 'Each request must carry a recursive proof')
    require(len({m['chainId'] for m in manifests}) == len({m['sender'] for m in manifests}) == 1, 'Manifests must share chain ID and sender')
    require([e['nonce'] for e in entries] == list(range(entries[0]['nonce'], entries[0]['nonce'] + len(entries))), 'Request nonces must be consecutive and ordered')
    require(all(m['nativeProfile'] == manifests[0]['nativeProfile'] for m in manifests), 'Manifests must share the exact native profile')
    triples = lambda entry: {int(d['scheme'], 16).to_bytes(32, 'big').hex() + d['dataHash'][2:].lower() + d['verificationKey'][2:].lower() for d in entry['dependencies']}
    expected = set().union(*(triples(e) for e in entries))
    require(len(expected) == 2 and {int(t[62:64], 16) for t in expected} == {16, 17}, 'Manifests must cover exactly one SPH and one generic claim')
    original = read_json(a.observe_report) if a.observe_report else None
    if original is not None:
        require(a.out.resolve() != a.observe_report.parent.resolve(), 'Recovery output must preserve the original report')
        require(original['mode'] == a.mode and original['nativeProfile'] == manifests[0]['nativeProfile'], 'Recovery mode and native profile must match the original report')
        require(original['manifests'] == [hashlib.sha256(path.read_bytes()).hexdigest() for path in a.manifest], 'Recovery manifests must match the original report hashes')
        observed = [e for e in original['admissions'] if e.get('accepted') or e.get('admissionUncertain')]
        require(observed and len({e['transactionHash'] for e in observed}) == len(observed), 'Recovery requires unique accepted or uncertain transaction hashes')
        by_hash = {e['transactionHash']: e for e in entries}
        require(all(e['transactionHash'] in by_hash and e['nonce'] == by_hash[e['transactionHash']]['nonce'] for e in observed), 'Recovery transaction hashes and nonces must belong to the signed manifests')
        entries = [by_hash[e['transactionHash']] for e in observed]
    a.out.mkdir(parents=True, exist_ok=False)
    rpc = [Rpc(f'http://127.0.0.1:{port}', timeout=5) for port in [a.rpc_port1, a.rpc_port2]]
    beacons = [f'http://127.0.0.1:{port}' for port in [a.beacon_port1, a.beacon_port2]]
    report = {'startedUtc': utc(), 'mode': a.mode, 'sourceRevision': a.source,
              'driverSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'nativeProfile': manifests[0]['nativeProfile'], 'completed': False,
              'nativeProofsVerified': False, 'completedFinality': False,
              'measurementScope': 'finite mixed-proof functionality; not throughput or spam capacity',
              'manifests': [hashlib.sha256(path.read_bytes()).hexdigest() for path in a.manifest],
              'admissions': original['admissions'] if original else [], 'blocks': [], 'health': [],
              'observationOnly': original is not None, 'canonicalObservations': [], 'readRetries': 0}
    if original:
        report['admissions'] = [dict(e) for e in report['admissions'] if e.get('accepted') or e.get('admissionUncertain')]
        report['originalReport'] = a.observe_report.name
        report['originalReportSha256'] = hashlib.sha256(a.observe_report.read_bytes()).hexdigest()
        report['originalBlocks'] = original.get('blocks', [])
        report['measurementScope'] = 'passive canonical receipt/proof/beacon/finality recovery; no submissions or new propagation measurement'

    def save():
        tmp = a.out / 'report.tmp'
        tmp.write_text(json.dumps(report, indent=2) + '\n')
        tmp.replace(a.out / 'report.json')

    def health():
        v = read_json(a.health_file)
        validate_health(v)
        report['health'].append(v)
        report['health'] = report['health'][-300:]

    def read_beacon(node, path):
        return retry_read(lambda: beacon(beacons[node], path, timeout=5), record_retry)

    def el(node, method, params):
        return retry_read(lambda: rpc[node].call(method, params), record_retry)

    def record_retry(error):
        health()
        report['readRetries'] += 1
        report['lastReadRetry'] = str(error)[:300]
        save()

    def bid(node, slot):
        v = read_beacon(node, f'/eth/v2/beacon/blocks/{slot}')
        require(not v.get('execution_optimistic', False), 'Beacon bid must not be execution optimistic')
        return v['data']['message']['body']['signed_execution_payload_bid']['message']

    def observe_receipts():
        health()
        if not original:
            for admission in report['admissions']:
                tx = el(1, 'eth_getTransactionByHash', [admission['transactionHash']])
                if tx is not None and 'gossipSeenBeforeReceipt' not in admission:
                    admission['gossipSeenBeforeReceipt'] = tx.get('blockHash') is None
        pairs = canonical_receipts(rpc, report['admissions'], record_retry)
        for admission, pair in zip(report['admissions'], pairs):
            receipts = [{k: r[k] for k in ['transactionHash', 'status', 'blockHash', 'blockNumber', 'gasUsed']} for r in pair]
            old = admission.get('receipts')
            if old and old[0]['blockHash'] != receipts[0]['blockHash']:
                report['canonicalObservations'].append({'transactionHash': admission['transactionHash'],
                    'oldBlockHash': old[0]['blockHash'], 'blockHash': receipts[0]['blockHash'], 'observedUtc': utc()})
                report['canonicalObservations'] = report['canonicalObservations'][-32:]
            admission['receipts'] = receipts
        save()
        return pairs

    verified_blocks = {}

    def observe_blocks():
        pairs = observe_receipts()
        block_hashes = {pair[0]['blockHash'] for pair in pairs}
        result = []
        for i, block_hash in enumerate(sorted(block_hashes)):
            pair = [el(n, 'eth_getBlockByHash', [block_hash, True]) for n in range(2)]
            if not all(pair):
                raise ObservationMoved('Receipt block unavailable')
            require(all(b['hash'] == block_hash for b in pair) and pair[0]['transactionsRoot'] == pair[1]['transactionsRoot'], 'Execution block hashes or transaction roots differ')
            block = pair[0]
            counts = dependencies(block)
            included = [e for e in entries if any(t['hash'] == e['transactionHash'] for t in block['transactions'])]
            observed = set().union(*(triples(e) for e in included))
            require(counts['canonicalTriplesSha256'] == hashlib.sha256(bytes.fromhex(''.join(sorted(observed)))).hexdigest(), 'Block dependencies differ from the included signed claims')
            require(len(block['transactions']) == len(included), 'Block contains transactions outside the observed signed admissions')
            slot = quantity(block['slotNumber'])
            anchors = [bid(n, slot) for n in range(2)]
            if not all(b['block_hash'] == block_hash for b in anchors):
                raise ObservationMoved('Beacon bid does not yet match current receipt block')
            ensure_canonical(rpc, block, record_retry)
            if block_hash not in verified_blocks:
                payload = a.out / f'payload-{block_hash[2:]}.json'
                try:
                    subprocess.run(['docker', 'cp', a.relay + ':/proof-cache/engine-captures/' + block_hash[2:] + '.json', str(payload)], check=True, capture_output=True, timeout=15)
                except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
                    raise RuntimeError('Capture unavailable or evicted for block ' + block_hash) from error
                engine = read_json(payload)
                require(engine['method'] == 'engine_newPayloadV5' and engine['params'][0]['blockHash'] == block_hash, 'Captured Engine payload must identify the observed block')
                proof = bytes.fromhex(engine['params'][0]['recursiveStarkProof'][2:])
                require(len(proof) >= 12 and proof[:4] == b'NLR3', 'Proof must use the current NLR3 envelope')
                n = struct.unpack_from('<I', proof, 4)[0]
                require(n == len(observed) and proof[8:8 + 96*n].hex() == ''.join(sorted(observed)), 'Proof dependencies differ from the observed canonical claims')
                offset = 8 + 96*n
                require(offset + 4 <= len(proof), 'Proof dependency table exceeds the envelope')
                size = struct.unpack_from('<I', proof, offset)[0]
                require(len(proof) == offset + 4 + size and size > 0, 'Proof envelope length is invalid or its mixed payload is empty')
                checked = a.out / f'payload-{block_hash[2:]}-verified.json'
                subprocess.run([a.dotnet, a.helper, '--devnet-payload=' + str(payload), '--mutation=none', '--out=' + str(checked)], check=True, timeout=30)
                inspection = read_json(pathlib.Path(str(checked) + '.inspection.json'))
                require(inspection['originalNativeProofValid'] and inspection['resultingNativeProofValid'] and inspection['canonicalHeaderHashMatches'], 'Native proof verification or canonical header hash check failed')
                require(set(inspection['transactionHashes']) == {e['transactionHash'] for e in included}, 'Native inspection transaction hashes differ from the signed admissions')
                verified_blocks[block_hash] = {'blockHash': block_hash, 'number': block['number'], 'slot': slot,
                    'aggregation': counts, 'proofBytes': len(proof), 'proofSha256': hashlib.sha256(proof).hexdigest(),
                    'payloadSha256': hashlib.sha256(payload.read_bytes()).hexdigest(),
                    'nativeInspection': inspection, 'beaconAnchoredBoth': True, 'finalizedBoth': False}
            result.append(dict(verified_blocks[block_hash]))
        latest = observe_receipts()
        if [pair[0]['blockHash'] for pair in latest] != [pair[0]['blockHash'] for pair in pairs]:
            raise ObservationMoved('Receipt placement changed during proof verification')
        report['blocks'] = result
        report['freshMixedParentInOneBlock'] = a.mode == 'merge' and len(entries) == 2 and len(block_hashes) == 1
        report['nativeProofsVerified'] = True
        save()
        return result

    def observe_until(deadline, action):
        last_error = None
        while time.monotonic() < deadline:
            try:
                return action()
            except Exception as error:
                if not isinstance(error, ObservationMoved) and not transient_read(error):
                    raise
                last_error = error
                record_retry(error)
                time.sleep(.5)
        raise TimeoutError('Canonical observation deadline expired; last=' + str(last_error))

    try:
        health()
        require(all(quantity(node.call('eth_chainId', [])) == manifests[0]['chainId'] for node in rpc), 'Execution chain ID differs from the manifests')
        if not original:
            require(all(quantity(node.call('eth_getTransactionCount', [manifests[0]['sender'], 'latest'])) == entries[0]['nonce'] for node in rpc), 'Sender nonce differs from the first signed request')
        report['versions'] = [node.call('web3_clientVersion', []) for node in rpc]
        report['peers'] = [node.call('admin_peers', []) for node in rpc]
        require(all(any('lean/1' in peer.get('caps', []) for peer in peers) for peers in report['peers']), 'Both execution nodes must have a negotiated lean/1 peer')
        require(all(node.call('eth_syncing', []) is False for node in rpc), 'Both execution nodes must be synchronized')
        for n in range(2):
            sync = read_beacon(n, '/eth/v1/node/syncing')['data']
            require(not sync['is_syncing'] and not sync['is_optimistic'] and not sync['el_offline'] and quantity(sync['sync_distance']) == 0, 'Beacon node must be synchronized, online and nonoptimistic')
        if not original:
            require(all(node.call('txpool_status', []) == {'pending': '0x0', 'queued': '0x0'} for node in rpc), 'Execution pools must be empty before offering requests')
            old = rpc[0].call('eth_getBlockByNumber', ['latest', False])['hash']
            wait_for(lambda: [node.call('eth_getBlockByNumber', ['latest', False]) for node in rpc],
                     lambda pair: pair[0]['hash'] != old and pair[0]['hash'] == pair[1]['hash'], 60, 'fresh matching execution head')
            for path, entry in zip(a.manifest, entries):
                health()
                request = read_json(path.parent / basename(entry['requestFile']))
                require(request['method'] == 'eth_sendProofWrapper', 'Request method must be eth_sendProofWrapper')
                admission = {'transactionHash': entry['transactionHash'], 'nonce': entry['nonce'], 'offeredUtc': utc(), 'accepted': False, 'admissionUncertain': True}
                report['admissions'].append(admission)
                save()
                try:
                    submit_wrapper(rpc[0], request, admission)
                finally:
                    save()
        observe_until(time.monotonic() + 180, observe_blocks)
        report['peerPoolPropagationObserved'] = original.get('peerPoolPropagationObserved', False) if original else all(e.get('gossipSeenBeforeReceipt') for e in report['admissions'])
        report['receiptsCompletedUtc'] = utc()
        report['drainedNonces'] = [quantity(node.call('eth_getTransactionCount', [manifests[0]['sender'], 'latest'])) for node in rpc]
        require(all(n >= entries[-1]['nonce'] + 1 for n in report['drainedNonces']) if original else report['drainedNonces'] == [entries[-1]['nonce'] + 1] * 2, 'Drained sender nonces do not cover the observed signed admissions')
        wait_for(lambda: [node.call('txpool_status', []) for node in rpc], lambda ps: all(x == {'pending': '0x0', 'queued': '0x0'} for x in ps), 30, 'empty pools')
        deadline = time.monotonic() + a.finality_timeout
        while time.monotonic() < deadline:
            health()
            try:
                observe_blocks()
                responses = [read_beacon(n, '/eth/v1/beacon/states/head/finality_checkpoints') for n in range(2)]
                require(all(not response.get('execution_optimistic', False) for response in responses), 'Finality response must not be execution optimistic')
                checkpoints = [response['data']['finalized'] for response in responses]
                report['finalityCheckpoints'] = checkpoints
                for block in report['blocks']:
                    if all(quantity(c['epoch']) * 32 > block['slot'] for c in checkpoints):
                        ensure_canonical(rpc, {'number': block['number'], 'hash': block['blockHash']}, record_retry)
                        ensure_el_finalized(rpc, {'number': block['number'], 'hash': block['blockHash']}, record_retry)
                        block['finalizedBoth'] = True
                if all(b['finalizedBoth'] for b in report['blocks']):
                    for n in range(2):
                        sync = read_beacon(n, '/eth/v1/node/syncing')['data']
                        require(not sync['is_syncing'] and not sync['is_optimistic'] and not sync['el_offline'] and quantity(sync['sync_distance']) == 0, 'Final beacon health must be synchronized, online and nonoptimistic')
            except Exception as error:
                if not isinstance(error, ObservationMoved) and not transient_read(error):
                    raise
                for block in report['blocks']:
                    block['finalizedBoth'] = False
                record_retry(error)
                time.sleep(1)
                continue
            save()
            if all(b['finalizedBoth'] for b in report['blocks']):
                break
            time.sleep(5)
        report['completedFinality'] = all(b['finalizedBoth'] for b in report['blocks'])
        report['completed'] = report['completedFinality'] and (original is not None or report['peerPoolPropagationObserved']) and (a.mode == 'reuse' or report['freshMixedParentInOneBlock'])
        if not report['completed']:
            report['error'] = 'Finality not observed' if not report['completedFinality'] else 'Peer pool propagation not observed' if not report['peerPoolPropagationObserved'] else 'Separate inclusion does not establish a fresh mixed parent'
    except Exception as error:
        report['error'] = str(error)[:1000]
        raise
    finally:
        report['finishedUtc'] = utc()
        save()
        print(json.dumps({k: report.get(k) for k in ['mode', 'completed', 'nativeProofsVerified', 'freshMixedParentInOneBlock', 'completedFinality', 'error']}), flush=True)
    if not report['completed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
