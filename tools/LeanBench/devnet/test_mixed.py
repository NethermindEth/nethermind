# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Offline observer failure/reorg tests; native proof acceptance is validated separately."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mixed


class MixedObserverChecks(unittest.TestCase):
    def test_transient_reads_retry_but_validation_and_native_errors_do_not(self):
        failures = [TimeoutError('slow beacon'), urllib.error.HTTPError('local', 404, 'not yet available', {}, None)]
        retries = []
        def read():
            if failures:
                raise failures.pop(0)
            return {'current': True}
        with patch.object(mixed.time, 'sleep'):
            self.assertEqual(mixed.retry_read(read, retries.append), {'current': True})
        self.assertEqual(len(retries), 2)
        for failure in [AssertionError('invalid anchor shape'), subprocess.CalledProcessError(1, 'native-check'),
                        urllib.error.HTTPError('local', 401, 'unauthorized', {}, None)]:
            with self.subTest(failure=type(failure).__name__), patch.object(mixed.time, 'sleep') as delay:
                def fail():
                    raise failure
                with self.assertRaises(type(failure)):
                    mixed.retry_read(fail)
                delay.assert_not_called()

    def test_orphaned_receipts_are_reobserved_and_recovery_never_submits(self):
        self.run_recovery()

    def test_native_verification_failure_aborts_recovery_without_retry(self):
        self.run_recovery(native_failure=True)

    def test_final_success_health_timeout_cannot_leave_stale_finalized_flags(self):
        self.run_recovery(final_sync_timeout=True)

    def test_optimistic_finality_is_rejected(self):
        self.run_recovery(optimistic=True)

    def test_ambiguous_offer_is_preserved_without_confirmed_rpc_admission(self):
        for result in [TimeoutError('response lost'), ['unexpected hash'], mixed.RpcError('eth_sendProofWrapper', {'message': 'rejected'})]:
            with self.subTest(result=result):
                class OfferRpc:
                    def call(self, method, params):
                        self.method = method
                        if isinstance(result, Exception):
                            raise result
                        return result
                admission = {'transactionHash': 'signed-hash', 'accepted': False}
                node = OfferRpc()
                with self.assertRaises((RuntimeError, TimeoutError)):
                    mixed.submit_wrapper(node, {'method': 'eth_sendProofWrapper', 'params': ['signed-wrapper']}, admission)
                self.assertEqual(node.method, 'eth_sendProofWrapper')
                self.assertFalse(admission['accepted'])
                self.assertEqual(admission['admissionUncertain'], not isinstance(result, mixed.RpcError))

    def test_uncertain_offer_with_canonical_receipts_recovers_without_resubmission(self):
        self.run_recovery(uncertain=True)

    def test_absent_uncertain_offer_has_explicit_bounded_failure(self):
        self.run_recovery(uncertain=True, missing_uncertain=True)

    def test_capture_eviction_has_clear_error_and_preserves_cause(self):
        self.run_recovery(capture_missing=True)

    def test_execution_finalized_tag_must_cover_a_canonical_ancestor(self):
        anchored = {'number': '0x10', 'hash': '0x' + '11' * 32}
        finalized = {'number': '0x20', 'hash': '0x' + '22' * 32}
        class FinalizedRpc:
            def __init__(self, tag):
                self.tag = tag
                self.requests = []
            def call(self, method, params):
                self.requests.append(params[0])
                if params[0] == 'finalized': return self.tag
                return finalized if params[0] == '0x20' else anchored
        node = FinalizedRpc(finalized)
        mixed.ensure_el_finalized([node], anchored)
        self.assertEqual(node.requests, ['finalized', '0x20', '0x10'])
        for tag in [None, {'number': '0xf', 'hash': anchored['hash']}, {'number': '0x20', 'hash': anchored['hash']}]:
            with self.subTest(tag=tag), self.assertRaises(mixed.ObservationMoved):
                mixed.ensure_el_finalized([FinalizedRpc(tag)], anchored)
        for tag in [{'number': 'invalid', 'hash': finalized['hash']},
                    {'number': '0x20', 'hash': '22' * 32},
                    {'number': '0x20', 'hash': '0x' + 'gg' * 32},
                    {'number': '0x20', 'hash': '0x' + '22' * 31},
                    {'number': '0x20', 'hash': finalized['hash'] + '\n'}]:
            with self.subTest(tag=tag), self.assertRaisesRegex(RuntimeError, 'Malformed execution finalized block'):
                mixed.ensure_el_finalized([FinalizedRpc(tag)], anchored)

    def test_health_gate_remains_enabled_in_optimized_python(self):
        sample = {'capturedEpoch': time.time(), 'hostAvailableBytes': 5 * 1024**3,
                  'hostAvailableFloorBytes': 6 * 1024**3, 'hostCpuPercent': 10,
                  'elMemoryBytes': [100, 100], 'elMemoryLimits': [1000, 1000],
                  'elOomKilled': [False, False], 'elRestarts': [0, 0]}
        result = subprocess.run([sys.executable, '-O', '-c',
            'import json, mixed, sys; mixed.validate_health(json.loads(sys.argv[1]))', json.dumps(sample)],
            cwd=Path(__file__).resolve().parent, capture_output=True, text=True,
            env={**os.environ, 'PYTHONDONTWRITEBYTECODE': '1'}, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('RuntimeError: Host available memory is below the safety floor', result.stderr)

    def run_recovery(self, native_failure=False, final_sync_timeout=False, optimistic=False,
                     uncertain=False, missing_uncertain=False, capture_missing=False):
        old_hash, new_hash = '0x' + '11' * 32, '0x' + '22' * 32
        hashes = ['0x' + '33' * 32, '0x' + '44' * 32]
        claims = [{'scheme': hex(scheme), 'dataHash': '0x' + value * 32, 'verificationKey': '0x' + '77' * 32}
                  for scheme, value in [(16, '55'), (17, '66')]]
        triples = [(int(d['scheme'], 16).to_bytes(32, 'big') + bytes.fromhex(d['dataHash'][2:] + d['verificationKey'][2:])) for d in claims]
        block = {'hash': new_hash, 'number': '0x10', 'slotNumber': '0x3c', 'transactionsRoot': '0x' + '88' * 32,
                 'transactions': [{'hash': h, 'frames': [{'mode': '0x4', 'data': '0x' + triple.hex()}]}
                                  for h, triple in zip(hashes, triples)]}
        state = {'orphan': True, 'nativeCalls': 0, 'anchorReads': 0, 'finalityReads': 0, 'syncReads': 0, 'deadlineExpired': False}

        class ObserverRpc:
            def __init__(self, *args, **kwargs):
                pass
            def call(self, method, params):
                if method == 'eth_chainId': return hex(10088289)
                if method == 'web3_clientVersion': return 'offline-observer-check'
                if method == 'admin_peers': return [{'caps': ['lean/1']}]
                if method == 'eth_syncing': return False
                if method == 'eth_getTransactionCount': return '0x3'
                if method == 'txpool_status': return {'pending': '0x0', 'queued': '0x0'}
                if method == 'eth_getTransactionReceipt':
                    if missing_uncertain and params[0] == hashes[1]:
                        state['deadlineExpired'] = True
                        return None
                    return {'transactionHash': params[0], 'blockHash': old_hash if state['orphan'] else new_hash,
                            'blockNumber': '0x10', 'status': '0x1', 'gasUsed': '0x10000'}
                if method == 'eth_getBlockByNumber':
                    if params[0] == 'finalized': return block
                    if params[0] == 'latest': raise AssertionError('Recovery must skip the pre-offer head wait')
                    state['orphan'] = False
                    return block
                if method == 'eth_getBlockByHash':
                    if params[0] != new_hash: raise AssertionError('Orphan must not reach proof/anchor validation')
                    return block
                raise AssertionError('Recovery must not offer or measure new pool propagation: ' + method)

        def read_beacon(url, path, timeout=5):
            if path == '/eth/v1/node/syncing':
                state['syncReads'] += 1
                if final_sync_timeout and state['syncReads'] > 2:
                    state['deadlineExpired'] = True
                    raise TimeoutError('Final health read unavailable')
                return {'data': {'is_syncing': False, 'is_optimistic': False, 'el_offline': False, 'sync_distance': '0'}}
            if '/blocks/' in path:
                state['anchorReads'] += 1
                if state['anchorReads'] == 1:
                    raise urllib.error.HTTPError('local', 404, 'beacon block not ready', {}, None)
                self.assertTrue(path.endswith('/60'))
                return {'data': {'message': {'body': {'signed_execution_payload_bid': {'message': {'block_hash': new_hash}}}}}}
            if path.endswith('/finality_checkpoints'):
                state['finalityReads'] += 1
                if state['finalityReads'] <= 3: raise TimeoutError('Transient finality read timeout')
                return {'execution_optimistic': optimistic, 'data': {'finalized': {'epoch': '2', 'root': '0x' + '99' * 32}}}
            raise AssertionError(path)

        def subprocess_run(command, **kwargs):
            if command[:2] == ['docker', 'cp']:
                self.assertIn(new_hash[2:], command[2])
                if capture_missing: raise subprocess.CalledProcessError(1, command, stderr=b'capture does not exist')
                proof = b'NLR3' + struct.pack('<I', 2) + b''.join(sorted(triples)) + struct.pack('<I', 1) + b'x'
                Path(command[-1]).write_text(json.dumps({'method': 'engine_newPayloadV5',
                    'params': [{'blockHash': new_hash, 'recursiveStarkProof': '0x' + proof.hex()}]}))
            else:
                state['nativeCalls'] += 1
                if native_failure: raise subprocess.CalledProcessError(1, command)
                checked = next(value.removeprefix('--out=') for value in command if value.startswith('--out='))
                Path(checked + '.inspection.json').write_text(json.dumps({'originalNativeProofValid': True,
                    'resultingNativeProofValid': True, 'canonicalHeaderHashMatches': True, 'transactionHashes': hashes}))
            return subprocess.CompletedProcess(command, 0)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifests = []
            for i, (h, claim) in enumerate(zip(hashes, claims)):
                path = root / f'manifest-{i}.json'
                path.write_text(json.dumps({'chainId': 10088289, 'sender': '0x' + 'aa' * 20, 'nativeProfile': {'abi': 5},
                    'requests': [{'mode': 'recursive', 'nonce': i + 1, 'transactionHash': h, 'dependencies': [claim]}]}))
                manifests.append(path)
            original = root / 'original.json'
            original.write_text(json.dumps({'mode': 'merge', 'nativeProfile': {'abi': 5}, 'peerPoolPropagationObserved': False,
                'manifests': [hashlib.sha256(path.read_bytes()).hexdigest() for path in manifests], 'blocks': [],
                'admissions': [{'accepted': not (uncertain and i == 1), 'admissionUncertain': uncertain and i == 1, 'transactionHash': h, 'nonce': i + 1,
                    'receipts': [{'blockHash': old_hash}, {'blockHash': old_hash}]} for i, h in enumerate(hashes)]}))
            original_bytes = original.read_bytes()
            health = root / 'health.json'
            health.write_text(json.dumps({'capturedEpoch': time.time(), 'hostAvailableBytes': 10 * 1024**3,
                'hostAvailableFloorBytes': 6 * 1024**3, 'hostCpuPercent': 10, 'elMemoryBytes': [100, 100],
                'elMemoryLimits': [1000, 1000], 'elOomKilled': [False, False], 'elRestarts': [0, 0]}))
            output = root / 'observed'
            argv = ['mixed.py', '--manifest', str(manifests[0]), '--manifest', str(manifests[1]),
                    '--observe-report', str(original), '--out', str(output), '--mode', 'merge', '--relay', 'offline',
                    '--helper', 'offline-helper', '--source', 'offline-source', '--health-file', str(health), '--finality-timeout', '60']
            with patch.object(sys, 'argv', argv), patch.object(mixed, 'Rpc', ObserverRpc), \
                    patch.object(mixed, 'beacon', read_beacon), patch.object(mixed.subprocess, 'run', subprocess_run), \
                    patch.object(mixed.time, 'sleep'), \
                    patch.object(mixed.time, 'monotonic', lambda: 10000 if state['deadlineExpired'] else 0), contextlib.redirect_stdout(io.StringIO()):
                if missing_uncertain:
                    with self.assertRaisesRegex(TimeoutError, 'Uncertain admission has no receipt on both nodes'):
                        mixed.main()
                elif capture_missing:
                    with self.assertRaisesRegex(RuntimeError, 'Capture unavailable or evicted') as failure:
                        mixed.main()
                    self.assertIsInstance(failure.exception.__cause__, subprocess.CalledProcessError)
                elif native_failure:
                    with self.assertRaises(subprocess.CalledProcessError): mixed.main()
                elif final_sync_timeout:
                    with self.assertRaises(SystemExit): mixed.main()
                elif optimistic:
                    with self.assertRaisesRegex(RuntimeError, 'Finality response must not be execution optimistic'): mixed.main()
                else:
                    mixed.main()
            report = json.loads((output / 'report.json').read_text())
            self.assertEqual(original.read_bytes(), original_bytes)
            self.assertEqual(report['originalReportSha256'], hashlib.sha256(original_bytes).hexdigest())
            self.assertEqual(state['nativeCalls'], 0 if capture_missing or missing_uncertain else 1)
            if native_failure or optimistic or final_sync_timeout or capture_missing or missing_uncertain:
                self.assertFalse(report['completed'])
                self.assertFalse(report['completedFinality'])
                if native_failure: self.assertFalse(report['nativeProofsVerified'])
                if final_sync_timeout: self.assertTrue(all(not b['finalizedBoth'] for b in report['blocks']))
            else:
                self.assertTrue(report['completed'] and report['completedFinality'] and report['observationOnly'])
                self.assertFalse(report['peerPoolPropagationObserved'])
                self.assertTrue(report['freshMixedParentInOneBlock'])
                self.assertEqual(report['blocks'][0]['blockHash'], new_hash)
                self.assertEqual(len(report['canonicalObservations']), 2)
                self.assertGreaterEqual(report['readRetries'], 4)
                self.assertEqual(state['finalityReads'], 5)
                if uncertain:
                    self.assertFalse(report['admissions'][1]['accepted'])
                    self.assertTrue(report['admissions'][1]['admissionUncertain'])
                    self.assertEqual(len(report['admissions'][1]['receipts']), 2)


if __name__ == '__main__':
    unittest.main()
