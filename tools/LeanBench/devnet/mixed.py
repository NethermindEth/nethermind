#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import argparse
import hashlib
import json
import math
import pathlib
import struct
import subprocess
import sys
import time

p = argparse.ArgumentParser(description='Bounded real-CL mixed-proof validation; no Engine driving or submission retries.')
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
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from drive import Rpc, quantity, wait_for
from spam import beacon, dependencies, read_json, basename, utc

assert 0 <= a.finality_timeout <= 1800
assert all(1 <= port <= 65535 for port in (a.rpc_port1, a.rpc_port2, a.beacon_port1, a.beacon_port2))
assert len(a.manifest) == (1 if a.mode == 'reuse' else 2)
manifests = [read_json(path) for path in a.manifest]
assert all(len(m['requests']) == 1 and m['nativeProfile']['abi'] == 5 for m in manifests)
entries = [m['requests'][0] for m in manifests]
assert all(e['mode'] == 'recursive' for e in entries)
assert len({m['chainId'] for m in manifests}) == len({m['sender'] for m in manifests}) == 1
assert [e['nonce'] for e in entries] == list(range(entries[0]['nonce'], entries[0]['nonce'] + len(entries)))
assert all(m['nativeProfile'] == manifests[0]['nativeProfile'] for m in manifests)
triples = lambda entry: {int(d['scheme'], 16).to_bytes(32, 'big').hex() + d['dataHash'][2:].lower() + d['verificationKey'][2:].lower() for d in entry['dependencies']}
expected = set().union(*(triples(e) for e in entries))
assert len(expected) == 2 and {int(t[62:64], 16) for t in expected} == {16, 17}
a.out.mkdir(parents=True, exist_ok=False)
rpc = [Rpc(f'http://127.0.0.1:{port}', timeout=5) for port in [a.rpc_port1, a.rpc_port2]]
beacons = [f'http://127.0.0.1:{port}' for port in [a.beacon_port1, a.beacon_port2]]
report = {'startedUtc': utc(), 'mode': a.mode, 'sourceRevision': a.source,
          'driverSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
          'nativeProfile': manifests[0]['nativeProfile'], 'completed': False,
          'nativeProofsVerified': False, 'completedFinality': False,
          'measurementScope': 'finite mixed-proof functionality; not throughput or spam capacity',
          'manifests': [hashlib.sha256(path.read_bytes()).hexdigest() for path in a.manifest],
          'admissions': [], 'blocks': [], 'health': []}

def save():
    tmp = a.out / 'report.tmp'
    tmp.write_text(json.dumps(report, indent=2) + '\n')
    tmp.replace(a.out / 'report.json')

def health():
    v = read_json(a.health_file)
    assert not v.get('error') and math.isfinite(v['capturedEpoch']) and 0 <= time.time() - v['capturedEpoch'] <= 15
    assert len(v['elMemoryBytes']) == len(v['elMemoryLimits']) == 2
    assert all(isinstance(n, (float, int)) and math.isfinite(n) and n >= 0 for n in [v['hostAvailableBytes'], v['hostAvailableFloorBytes'], v['hostCpuPercent'], *v['elMemoryBytes'], *v['elMemoryLimits']])
    assert all(n > 0 for n in v['elMemoryLimits'])
    assert v['hostAvailableFloorBytes'] >= 6 * 1024**3
    assert v['hostAvailableBytes'] >= v['hostAvailableFloorBytes']
    assert v['hostCpuPercent'] < 85 and len(v['elMemoryBytes']) == 2
    assert all(x < y * .8 for x, y in zip(v['elMemoryBytes'], v['elMemoryLimits']))
    assert v['elOomKilled'] == [False, False] and v['elRestarts'] == [0, 0]
    report['health'].append(v)
    report['health'] = report['health'][-300:]

def read_beacon(node, path):
    return beacon(beacons[node], path, timeout=5)

def bid(node, slot):
    v = read_beacon(node, f'/eth/v2/beacon/blocks/{slot}')
    assert not v.get('execution_optimistic', False)
    return v['data']['message']['body']['signed_execution_payload_bid']['message']

def observe_receipts():
    health()
    result = []
    for admission in report['admissions']:
        tx = rpc[1].call('eth_getTransactionByHash', [admission['transactionHash']])
        if tx is not None and 'gossipSeenBeforeReceipt' not in admission:
            admission['gossipSeenBeforeReceipt'] = tx.get('blockHash') is None
        pair = [node.call('eth_getTransactionReceipt', [admission['transactionHash']]) for node in rpc]
        if all(pair):
            assert all(r['status'] == '0x1' for r in pair)
            assert pair[0]['blockHash'] == pair[1]['blockHash']
            admission['receipts'] = [{k: r[k] for k in ['transactionHash', 'status', 'blockHash', 'blockNumber', 'gasUsed']} for r in pair]
        result.append(pair)
    save()
    return result

try:
    health()
    assert all(quantity(node.call('eth_chainId', [])) == manifests[0]['chainId'] for node in rpc)
    assert all(quantity(node.call('eth_getTransactionCount', [manifests[0]['sender'], 'latest'])) == entries[0]['nonce'] for node in rpc)
    report['versions'] = [node.call('web3_clientVersion', []) for node in rpc]
    report['peers'] = [node.call('admin_peers', []) for node in rpc]
    assert all(any('lean/1' in peer.get('caps', []) for peer in peers) for peers in report['peers'])
    assert all(node.call('eth_syncing', []) is False for node in rpc)
    for n in range(2):
        sync = read_beacon(n, '/eth/v1/node/syncing')['data']
        assert not sync['is_syncing'] and not sync['is_optimistic'] and not sync['el_offline'] and quantity(sync['sync_distance']) == 0
    assert all(node.call('txpool_status', []) == {'pending': '0x0', 'queued': '0x0'} for node in rpc)
    old = rpc[0].call('eth_getBlockByNumber', ['latest', False])['hash']
    wait_for(lambda: [node.call('eth_getBlockByNumber', ['latest', False]) for node in rpc],
             lambda pair: pair[0]['hash'] != old and pair[0]['hash'] == pair[1]['hash'], 60, 'fresh matching execution head')
    for path, entry in zip(a.manifest, entries):
        health()
        request = read_json(path.parent / basename(entry['requestFile']))
        assert request['method'] == 'eth_sendProofWrapper'
        admission = {'transactionHash': entry['transactionHash'], 'nonce': entry['nonce'], 'offeredUtc': utc(), 'accepted': False}
        report['admissions'].append(admission)
        save()
        result = rpc[0].call(request['method'], request['params'])
        assert result == [entry['transactionHash']]
        admission.update(accepted=True, acceptedUtc=utc())
        save()
    pairs = wait_for(observe_receipts, lambda ps: all(all(pair) for pair in ps), 180, 'both execution receipts')
    report['peerPoolPropagationObserved'] = all(e.get('gossipSeenBeforeReceipt') for e in report['admissions'])
    report['receiptsCompletedUtc'] = utc()
    block_hashes = {pair[0]['blockHash'] for pair in pairs}
    report['freshMixedParentInOneBlock'] = a.mode == 'merge' and len(block_hashes) == 1
    for i, block_hash in enumerate(sorted(block_hashes)):
        pair = [node.call('eth_getBlockByHash', [block_hash, True]) for node in rpc]
        assert all(b['hash'] == block_hash for b in pair) and pair[0]['transactionsRoot'] == pair[1]['transactionsRoot']
        block = pair[0]
        counts = dependencies(block)
        included = [e for e in entries if any(t['hash'] == e['transactionHash'] for t in block['transactions'])]
        observed = set().union(*(triples(e) for e in included))
        assert counts['canonicalTriplesSha256'] == hashlib.sha256(bytes.fromhex(''.join(sorted(observed)))).hexdigest()
        assert len(block['transactions']) == len(included)
        slot = quantity(block['slotNumber'])
        wait_for(lambda: [bid(n, slot) for n in range(2)], lambda bs: all(b['block_hash'] == block_hash for b in bs), 30, 'real beacon bid anchors')
        payload = a.out / f'payload-{i:03}.json'
        subprocess.run(['docker', 'cp', a.relay + ':/proof-cache/engine-captures/' + block_hash[2:] + '.json', str(payload)], check=True, capture_output=True)
        engine = read_json(payload)
        assert engine['method'] == 'engine_newPayloadV5' and engine['params'][0]['blockHash'] == block_hash
        proof = bytes.fromhex(engine['params'][0]['recursiveStarkProof'][2:])
        assert proof[:4] == b'NLR3'
        n = struct.unpack_from('<I', proof, 4)[0]
        assert n == len(observed) and proof[8:8 + 96*n].hex() == ''.join(sorted(observed))
        offset = 8 + 96*n
        size = struct.unpack_from('<I', proof, offset)[0]
        assert len(proof) == offset + 4 + size and size > 0
        checked = a.out / f'payload-{i:03}-verified.json'
        subprocess.run([a.dotnet, a.helper, '--devnet-payload=' + str(payload), '--mutation=none', '--out=' + str(checked)], check=True, timeout=30)
        inspection = read_json(pathlib.Path(str(checked) + '.inspection.json'))
        assert inspection['originalNativeProofValid'] and inspection['resultingNativeProofValid'] and inspection['canonicalHeaderHashMatches']
        assert set(inspection['transactionHashes']) == {e['transactionHash'] for e in included}
        report['blocks'].append({'blockHash': block_hash, 'number': block['number'], 'slot': slot,
                                 'aggregation': counts, 'proofBytes': len(proof), 'proofSha256': hashlib.sha256(proof).hexdigest(),
                                 'payloadSha256': hashlib.sha256(payload.read_bytes()).hexdigest(),
                                 'nativeInspection': inspection, 'beaconAnchoredBoth': True, 'finalizedBoth': False})
        save()
    report['nativeProofsVerified'] = True
    report['drainedNonces'] = [quantity(node.call('eth_getTransactionCount', [manifests[0]['sender'], 'latest'])) for node in rpc]
    assert report['drainedNonces'] == [entries[-1]['nonce'] + 1] * 2
    wait_for(lambda: [node.call('txpool_status', []) for node in rpc], lambda ps: all(x == {'pending': '0x0', 'queued': '0x0'} for x in ps), 30, 'empty pools')
    deadline = time.monotonic() + a.finality_timeout
    while time.monotonic() < deadline:
        health()
        checkpoints = [read_beacon(n, '/eth/v1/beacon/states/head/finality_checkpoints')['data']['finalized'] for n in range(2)]
        report['finalityCheckpoints'] = checkpoints
        for block in report['blocks']:
            if all(quantity(c['epoch']) * 32 > block['slot'] for c in checkpoints):
                assert all(node.call('eth_getBlockByNumber', [block['number'], False])['hash'] == block['blockHash'] for node in rpc)
                assert all(bid(n, block['slot'])['block_hash'] == block['blockHash'] for n in range(2))
                block['finalizedBoth'] = True
        save()
        if all(b['finalizedBoth'] for b in report['blocks']):
            break
        time.sleep(5)
    report['completedFinality'] = all(b['finalizedBoth'] for b in report['blocks'])
    report['completed'] = report['completedFinality'] and report['peerPoolPropagationObserved'] and (a.mode == 'reuse' or report['freshMixedParentInOneBlock'])
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
