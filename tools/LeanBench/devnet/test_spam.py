# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Offline policy/counting tests; they do not substitute for live proof-load measurements."""
import datetime
import contextlib
import io
import time
import threading
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import spam


class SpamChecks(unittest.TestCase):
    def test_actual_body_counts_deduplicate_without_losing_raw_gas_declarations(self):
        sph = (16).to_bytes(32, 'big') + b'a' * 32 + b'b' * 32
        stark = (17).to_bytes(32, 'big') + b'c' * 32 + b'd' * 32
        block = {'transactions': [{'frames': [{'mode': '0x4', 'data': '0x' + (sph + sph + stark).hex()}]}]}
        counts = spam.dependencies(block)
        self.assertEqual(counts['rawDeclarations'], 3)
        self.assertEqual(counts['canonicalDependencies'], 2)
        self.assertEqual(counts['signatures'], 1)
        self.assertEqual(counts['genericStarks'], 1)
        broken = bytearray(sph)
        broken[0] = 1
        block['transactions'][0]['frames'][0]['data'] = '0x' + broken.hex()
        with self.assertRaisesRegex(RuntimeError, 'invalid'):
            spam.dependencies(block)

    def test_resource_failure_and_nonfinite_metrics_fail_closed(self):
        healthy = {'capturedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                   'hostAvailableBytes': 8 * 1024 ** 3, 'hostCpuPercent': 20,
                   'elMemoryPercent': [40, 40], 'elRestarts': [0, 0], 'elOomKilled': [False, False]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'health.json'
            path.write_text(json.dumps(healthy))
            self.assertEqual(spam.resource_guard(path)['hostCpuPercent'], 20)
            for changes in [{'hostCpuPercent': 86}, {'elMemoryPercent': [81, 40]},
                            {'hostCpuPercent': float('nan')}, {'elMemoryPercent': []},
                            {'elOomKilled': [True, False]}, {'error': 'collection failed'}]:
                with self.subTest(changes=changes):
                    path.write_text(json.dumps({**healthy, **changes}))
                    with self.assertRaises(RuntimeError):
                        spam.resource_guard(path)

    def test_control_plane_records_realistic_receipts_and_body_union(self):
        block_hash = '0x' + 'aa' * 32
        hashes = ['0x' + '11' * 32, '0x' + '22' * 32]
        claims = [{'scheme': '0x10', 'dataHash': '0x' + value * 32, 'verificationKey': '0x' + '44' * 32} for value in ['22', '33']]
        block = {'hash': block_hash, 'number': '0x1', 'timestamp': hex(int(time.time())), 'slotNumber': '0x20',
                 'gasUsed': '0x10000', 'gasLimit': '0x100000', 'transactionsRoot': '0x' + 'bb' * 32,
                 'transactions': [{'hash': key, 'frames': [{'mode': '0x4', 'data': '0x' + (16).to_bytes(32, 'big').hex() + claim['dataHash'][2:] + claim['verificationKey'][2:]}]} for key, claim in zip(hashes, claims)]}
        accepted = set()
        state = {'transportFailure': False, 'oom': False, 'allowSubmit': True, 'submissions': 0, 'negativeDelay': .03}
        lock = threading.Lock()

        class OfflineRpc:
            def __init__(self, *args, **kwargs):
                pass

            def call(self, method, params):
                with lock:
                    if method == 'eth_chainId': return hex(10088288)
                    if method == 'web3_clientVersion': return 'offline-control-test'
                    if method == 'eth_getTransactionCount': return hex(3 + len(accepted) + state.get('laterNonceAdvance', 0))
                    if method == 'eth_getBlockByNumber': return block
                    if method == 'txpool_status': return {'pending': hex(len(accepted)), 'queued': '0x0'}
                    if method == 'admin_peers': return [{'caps': ['lean/1']}]
                    if method == 'eth_sendProofWrapper':
                        if not state['allowSubmit']:
                            raise AssertionError('Recovery must never resubmit')
                        state['submissions'] += 1
                        if params[0].startswith('invalid-'):
                            time.sleep(state.get('negativeDelay', 0))
                            if state.get('busyProbe'):
                                raise spam.RpcError(method, {'code': -32000, 'message': 'Proof admission is busy; retry later.'})
                            raise spam.RpcError(method, {'code': -32000, 'message': spam.PROOF_ERROR})
                        accepted.add(params[0])
                        if state.get('positiveTimeout'):
                            raise TimeoutError('Valid RPC timed out after node admission')
                        return [params[0]]
                    if method == 'eth_getTransactionByHash':
                        if state['transportFailure'] and len(accepted) == 2:
                            state['oom'] = state.get('oomOnTransport', True)
                            if state.get('transient'):
                                state['transportFailure'] = False
                            raise ConnectionResetError('EL exited after OOM')
                        return {'hash': params[0], 'blockHash': block_hash if len(accepted) == 2 else None} if params[0] in accepted else None
                    if method == 'eth_getTransactionReceipt':
                        return {'status': '0x1', 'blockHash': block_hash, 'blockNumber': '0x1', 'gasUsed': '0x8000'} if len(accepted) == 2 else None
                    if method == 'eth_getBlockByHash': return block
                    raise AssertionError(method)

        def offline_beacon(url, path, timeout=5):
            if state.get('beaconFailure'):
                if state.get('beaconTransient'):
                    state['beaconFailure'] = False
                raise TimeoutError('CL read timed out')
            if path.endswith('/syncing'):
                return {'data': {'head_slot': '64', 'sync_distance': '0', 'is_optimistic': False, 'el_offline': False}}
            if path.endswith('/finality_checkpoints'):
                if state.get('advanceDuringFinality') and (root / 'out/report.json').is_file():
                    if json.loads((root / 'out/report.json').read_text()).get('receiptDrainCompletedUtc'):
                        state['laterNonceAdvance'] = 2
                return {'data': {'finalized': {'epoch': '2', 'root': '0x' + 'cc' * 32}}}
            after_drain = (root / 'out/report.json').is_file() and json.loads((root / 'out/report.json').read_text()).get('receiptDrainCompletedUtc')
            bid_hash = '0x' + 'dd' * 32 if state.get('reorgDuringFinality') and after_drain else block_hash
            return {'execution_optimistic': bool(state.get('optimisticDuringFinality') and after_drain),
                    'data': {'message': {'body': {'signed_execution_payload_bid': {'message': {'block_hash': bid_hash}}}}}}

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            entries = []
            for index, (key, claim) in enumerate(zip(hashes, claims)):
                name, negative = f'valid{index}.json', f'invalid{index}.json'
                (root / name).write_text(json.dumps({'method': 'eth_sendProofWrapper', 'params': [key]}))
                (root / negative).write_text(json.dumps({'method': 'eth_sendProofWrapper', 'params': ['invalid-' + key]}))
                entries.append({'name': name, 'nonce': 3 + index, 'mode': 'direct' if index == 0 else 'recursive',
                                'transactionHash': key, 'requestFile': name, 'negativeRequestFile': negative, 'dependencies': [claim]})
            (root / 'manifest.json').write_text(json.dumps({'chainId': 10088288, 'firstNonce': 3, 'sender': 'offline', 'requests': entries}))
            arguments = ['spam.py', '--manifest=' + str(root / 'manifest.json'), '--out=' + str(root / 'out'),
                         '--source-revision=offline', '--rate=2', '--limit=2', '--finality-timeout=1']
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            report = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(report['completed'])
            self.assertEqual(report['interarrivalMetricScope'], 'valid-wrapper RPC starts')
            arrivals = sorted(item['validOfferedSeconds'] for item in report['admissions'])
            self.assertAlmostEqual(report['observedInterarrivalOffersPerSecond'], 1 / (arrivals[1] - arrivals[0]))
            self.assertTrue(all(item['validOfferedSeconds'] - item['offeredSeconds'] >= .025 for item in report['admissions']))
            state['negativeDelay'] = 0
            self.assertEqual(report['includedTransactions'], 2)
            self.assertTrue(report['inclusionVerified'])
            self.assertTrue(report['completedFinality'])
            self.assertEqual(report['receiptDrainSenderNonces'], [5, 5])
            self.assertEqual(report['finalSenderNonces'], [5, 5])
            self.assertEqual(report['blocks'][0]['aggregation']['signatures'], 2)
            self.assertTrue(report['blocks'][0]['bodyDependencyUnionMatchesManifest'])
            self.assertTrue(report['blocks'][0]['finalizedBoth'])
            self.assertTrue(report['blocks'][0]['finalityBeaconRecheckedBoth'])
            invalid_arguments = arguments + ['--invalid-only', '--invalid-repeat=2']
            submissions = state['submissions']
            with patch.object(sys, 'argv', invalid_arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            invalid = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(invalid['completed'])
            self.assertEqual(invalid['acceptedTransactions'], 0)
            self.assertEqual(invalid['invalidOutcomes']['proofRejected'], 4)
            self.assertTrue(all(item['preexistingTransaction'] for item in invalid['admissions']))
            self.assertEqual(invalid['initialSenderNonces'], [5, 5])
            self.assertTrue(invalid['invalidIngressSenderNoncesUnchanged'])
            self.assertEqual(invalid['negativeTemplateCount'], 2)
            self.assertEqual(invalid['negativeAttemptCount'], 4)
            self.assertEqual(state['submissions'] - submissions, 4)
            accepted.clear()
            state['busyProbe'] = True
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit):
                    spam.main()
            busy = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(busy['completed'])
            self.assertIn('backpressure', busy['stopReason'])
            self.assertTrue(all(not item['validSubmissionOffered'] and not item['negative']['passed'] for item in busy['admissions']))
            self.assertEqual(busy['invalidOutcomes']['proofRejected'], 0)
            self.assertFalse(accepted)
            self.assertTrue(busy['stopEvents'])
            state['busyProbe'] = False
            accepted.clear()
            state['positiveTimeout'] = True
            timeout_arguments = [arg for arg in arguments if not arg.startswith('--limit=')] + ['--limit=1']
            with patch.object(sys, 'argv', timeout_arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'Sender nonces differ'):
                    spam.main()
            uncertain = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(uncertain['completed'])
            self.assertEqual(len(accepted), 1)
            self.assertEqual(len(uncertain['admissions']), 1)
            admission = uncertain['admissions'][0]
            self.assertTrue(admission['validSubmissionOffered'])
            self.assertTrue(admission['admissionUncertain'])
            self.assertFalse(admission['accepted'])
            self.assertEqual(uncertain['acceptedTransactions'], 0)
            state['positiveTimeout'] = False
            accepted.clear()
            state['advanceDuringFinality'] = True
            (root / 'out/report.json').unlink()
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            advanced = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(advanced['completed'])
            self.assertEqual(advanced['receiptDrainSenderNonces'], [5, 5])
            self.assertEqual(advanced['finalSenderNonces'], [7, 7])
            state.update(advanceDuringFinality=False, laterNonceAdvance=0)
            accepted.clear()
            zero_deadline = [arg for arg in arguments if not arg.startswith('--finality-timeout=')] + ['--finality-timeout=0']
            with patch.object(sys, 'argv', zero_deadline), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit):
                    spam.main()
            unfinalized = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(unfinalized['inclusionVerified'])
            self.assertFalse(unfinalized['completedFinality'])
            self.assertFalse(unfinalized['completed'])
            for scenario, expected_error in [('reorgDuringFinality', 'canonical beacon bid'), ('optimisticDuringFinality', 'bid is optimistic')]:
                accepted.clear()
                state[scenario] = True
                (root / 'out/report.json').unlink()
                with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon):
                    with self.assertRaisesRegex(RuntimeError, expected_error):
                        spam.main()
                reorged = json.loads((root / 'out/report.json').read_text())
                self.assertTrue(reorged['inclusionVerified'])
                self.assertFalse(reorged['completedFinality'])
                self.assertFalse(reorged['blocks'][0]['finalizedBoth'])
                self.assertFalse(reorged['completed'])
                state[scenario] = False
            accepted.clear()
            state['transportFailure'] = True
            def resources(path):
                if state['oom']:
                    raise RuntimeError('Execution-node resource stop threshold: OOM')
                return None
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), patch.object(spam, 'resource_guard', resources):
                with self.assertRaisesRegex(RuntimeError, 'OOM'):
                    spam.main()
            failed = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(failed['completed'])
            self.assertEqual(failed['acceptedTransactions'], 2)
            self.assertEqual(failed['observerTransportRetries'], 0)
            # Frozen f600 accepted admissions have flags but no actual RPC-start timestamps.
            for item in failed['admissions']:
                self.assertTrue(item['validSubmissionOffered'])
                item.pop('validOfferedSeconds', None)
                item.pop('negativeOfferedSeconds', None)
            (root / 'out/report.json').write_text(json.dumps(failed))
            state.update(transportFailure=False, oom=False, allowSubmit=False)
            submissions = state['submissions']
            state['laterNonceAdvance'] = 2
            recovery = [arg for arg in arguments if not arg.startswith('--out=')] + [
                '--out=' + str(root / 'recovery'), '--observe-report=' + str(root / 'out/report.json')]
            with patch.object(sys, 'argv', recovery), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            recovered = json.loads((root / 'recovery/report.json').read_text())
            self.assertTrue(recovered['completed'])
            self.assertEqual(state['submissions'], submissions)
            self.assertIsNone(recovered['goodputIncludingDrainTxPerSecond'])
            self.assertEqual(recovered['includedTransactions'], 2)
            self.assertEqual(recovered['receiptDrainSenderNonces'], [7, 7])
            self.assertEqual(recovered['finalSenderNonces'], [7, 7])
            state['laterNonceAdvance'] = 0
            accepted.clear()
            state.update(transportFailure=True, transient=True, oomOnTransport=False, oom=False, allowSubmit=True)
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            retried = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(retried['completed'])
            self.assertEqual(retried['observerTransportRetries'], 1)
            accepted.clear()
            state.update(transportFailure=True, transient=False, oom=False)
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon):
                with self.assertRaisesRegex(RuntimeError, 'rpc observer node[12] eth_getTransactionByHash'):
                    spam.main()
            persistent = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(persistent['completed'])
            self.assertEqual(persistent['observerTransportRetries'], 3)
            accepted.clear()
            state.update(transportFailure=False, beaconFailure=True, beaconTransient=True, oom=False)
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon), contextlib.redirect_stdout(io.StringIO()):
                spam.main()
            beacon_retry = json.loads((root / 'out/report.json').read_text())
            self.assertTrue(beacon_retry['completed'])
            self.assertEqual(beacon_retry['observerBeaconRetries'], 1)
            self.assertTrue(any(item.get('error') == 'CL read timed out' and item['target'] == '/eth/v1/node/syncing' for item in beacon_retry['readDiagnostics']))
            accepted.clear()
            state.update(beaconFailure=True, beaconTransient=False)
            with patch.object(sys, 'argv', arguments), patch.object(spam, 'Rpc', OfflineRpc), patch.object(spam, 'beacon', offline_beacon):
                with self.assertRaisesRegex(RuntimeError, 'beacon observer node1 /eth/v1/node/syncing'):
                    spam.main()
            beacon_failed = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(beacon_failed['completed'])
            self.assertEqual(beacon_failed['observerBeaconRetries'], 3)

    def test_unbounded_or_nonfinite_plans_reject_before_manifest_reads(self):
        for argument in ['--rate=nan', '--rate=inf', '--limit=65', '--workers=3', '--finality-timeout=inf']:
            with self.subTest(argument=argument), patch.object(sys, 'argv', [
                    'spam.py', '--manifest=/not-read', '--out=/not-created', '--source-revision=offline', argument]):
                with self.assertRaises(SystemExit) as error:
                    spam.main()
                self.assertEqual(error.exception.code, 2)


if __name__ == '__main__':
    unittest.main()
