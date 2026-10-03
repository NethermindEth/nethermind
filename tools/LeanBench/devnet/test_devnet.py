"""Offline control-plane checks; native proof validation is exercised by LeanBench."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


DIRECTORY = Path(__file__).resolve().parent


def load(name):
    spec = importlib.util.spec_from_file_location(name, DIRECTORY / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DevnetChecks(unittest.TestCase):
    def test_omitted_batch_preserves_payload_before_failure(self):
        driver = load('drive')
        tx_hash = '0x' + '22' * 32
        genesis = {'hash': driver.ZERO_HASH, 'number': '0x0', 'timestamp': '0x1', 'slotNumber': '0x0'}

        class FakeRpc:
            admitted = False
            rejection_message = "a wrapper dependency proof failed verification"

            def __init__(self, *args, **kwargs):
                pass

            def call(self, method, params):
                if method == 'eth_getBlockByNumber':
                    return genesis
                if method == 'eth_chainId':
                    return hex(10088288)
                if method == 'eth_getTransactionCount':
                    return '0x0'
                if method == 'web3_clientVersion':
                    return 'offline-control-check'
                if method == 'admin_nodeInfo':
                    return {'enode': 'offline'}
                if method == 'admin_addPeer':
                    return True
                if method == 'admin_peers':
                    return [{'id': 'offline', 'caps': ['lean/1']}]
                if method == 'eth_getTransactionByHash':
                    return {'hash': tx_hash, 'blockHash': None} if self.admitted else None
                if method == 'eth_sendProofWrapper':
                    if params == ['invalid']:
                        raise driver.RpcError(method, {'code': -32000, 'message': self.rejection_message})
                    FakeRpc.admitted = True
                    return [tx_hash]
                if method.startswith('engine_forkchoiceUpdated'):
                    return {'payloadStatus': {'status': 'VALID'}, 'payloadId': '0x0000000000000001'}
                if method == 'engine_getPayloadV6':
                    return {'executionPayload': {'blockHash': '0x' + '33' * 32, 'transactions': []}}
                raise AssertionError('Unexpected offline RPC method: ' + method)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = {'chainId': 10088288, 'firstNonce': 0, 'sender': '0x' + '11' * 20, 'requests': [{
                'name': 'omission-check', 'transactionHash': tx_hash, 'rawTransaction': '0x0102',
                'requestFile': 'valid.json', 'negativeRequestFile': 'invalid.json',
                'dependencies': [{'scheme': '0x10', 'dataHash': driver.ZERO_HASH, 'verificationKey': driver.ZERO_HASH}]}]}
            (root / 'manifest.json').write_text(json.dumps(manifest))
            (root / 'jwt.hex').write_text('11' * 32)
            for name, value in [('valid.json', 'valid'), ('invalid.json', 'invalid')]:
                (root / name).write_text(json.dumps({'method': 'eth_sendProofWrapper', 'params': [value]}))
            args = ['drive.py', '--manifest=' + str(root / 'manifest.json'), '--jwt=' + str(root / 'jwt.hex'),
                    '--out=' + str(root / 'out'), '--build-wait=0']
            with patch.object(driver, 'Rpc', FakeRpc), patch.object(sys, 'argv', args):
                with self.assertRaisesRegex(RuntimeError, 'transaction set/count differs'):
                    driver.main()
            saved = json.loads((root / 'out/get-payload-000.json').read_text())
            report = json.loads((root / 'out/report.json').read_text())
            self.assertEqual(saved['executionPayload']['transactions'], [])
            self.assertFalse(report['completed'])
            self.assertEqual(report['currentBuild']['expectedTransactions'], 1)
            self.assertEqual(report['currentBuild']['actualTransactions'], 0)
            FakeRpc.admitted = False
            FakeRpc.rejection_message = 'database unavailable'
            with patch.object(driver, 'Rpc', FakeRpc), patch.object(sys, 'argv', args):
                with self.assertRaisesRegex(RuntimeError, 'unexpected error'):
                    driver.main()
            unexpected = json.loads((root / 'out/report.json').read_text())
            self.assertFalse(unexpected['completed'])
            self.assertFalse(unexpected['negativeIngress'][0]['passed'])

    def test_nonfinite_timing_is_rejected_before_file_reads(self):
        driver = load('drive')
        for argument in ('--timeout=inf', '--timeout=nan', '--build-wait=inf', '--build-wait=nan'):
            with self.subTest(argument=argument), patch.object(sys, 'argv', [
                    'drive.py', '--manifest=/not-read', '--jwt=/not-read', '--out=/not-written', argument]):
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                    driver.main()
                self.assertEqual(error.exception.code, 2)

    def test_export_retains_hashes_without_opening_secrets_or_payload_bytes(self):
        capture = load('capture')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'jwt.hex').write_text('this must never be parsed or exported')
            (root / 'node1.json').write_text('not valid JSON')
            base = root / 'driver'
            base.mkdir()
            (base / 'report.json').write_text(json.dumps({'completed': False, 'blocks': [], 'error': 'recorded failure'}))
            payload = {'params': [{'blockHash': '0x' + '33' * 32, 'recursiveStarkProof': '0xcafe', 'transactions': ['untracked envelope']}]}
            (base / 'payload-000.json').write_text(json.dumps(payload))
            (base / 'receipts-000.json').write_text(json.dumps([[{'status': '0x1', 'blockHash': '0x33', 'logs': ['not retained']}]]))
            exported = capture.export(root)
            encoded = json.dumps(exported)
            self.assertNotIn('this must never', encoded)
            self.assertNotIn('untracked envelope', encoded)
            self.assertNotIn('not retained', encoded)
            artifact = exported['runs']['driver']['artifacts'][0]
            self.assertEqual(artifact['proofBytes'], 2)
            self.assertEqual(len(artifact['proofSha256']), 64)
            self.assertEqual(exported['runs']['driver']['report']['error'], 'recorded failure')

    def test_prepare_uses_separate_identities_shared_genesis_without_local_jwt(self):
        prepare = load('prepare')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = ['prepare.py', '--out=' + str(root), '--runtime-root=/tmp/lean-devnet-check', '--timestamp=1791035000']
            with patch.object(sys, 'argv', args):
                prepare.main()
            first = json.loads((root / 'node1.json').read_text())
            second = json.loads((root / 'node2.json').read_text())
            chain = json.loads((root / 'chain.json').read_text())
            self.assertEqual(first['Init']['ChainSpecPath'], second['Init']['ChainSpecPath'])
            self.assertNotEqual(first['Init']['BaseDbPath'], second['Init']['BaseDbPath'])
            self.assertNotEqual(first['KeyStore']['KeyStoreDirectory'], second['KeyStore']['KeyStoreDirectory'])
            self.assertEqual(first['Blocks']['SecondsPerSlot'], 12)
            self.assertEqual(chain['params']['eip8288TransitionTimestamp'], '0x0')
            self.assertFalse((root / 'jwt.hex').exists())


if __name__ == '__main__':
    unittest.main()
