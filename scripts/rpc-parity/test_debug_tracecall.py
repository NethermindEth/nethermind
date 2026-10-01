import copy
import importlib.util
import json
from pathlib import Path
import re
import unittest
from unittest.mock import Mock, patch
from typing import Any

HERE = Path(__file__).parent
SPEC = importlib.util.spec_from_file_location("debug_tracecall", HERE / "debug_tracecall.py")
assert SPEC and SPEC.loader
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


def load_corpus():
    return json.loads((HERE / "debug-tracecall-corpus.json").read_text())


class ParityRunnerTests(unittest.TestCase):
    def test_ssh_uses_short_private_socket_and_closes_it(self):
        directory = Mock(name='/var/folders/jb/wzcwsm2j3pbcpv0l_v9sx__h0000gn/T/tracecall-ssh-abcdefgh')
        directory.name = '/var/folders/jb/wzcwsm2j3pbcpv0l_v9sx__h0000gn/T/tracecall-ssh-abcdefgh'
        args = Mock(ssh_key='key', known_hosts='hosts')
        with patch.object(runner.tempfile, 'TemporaryDirectory', return_value=directory), patch.object(runner.subprocess, 'run') as execute:
            execute.return_value.stdout = b'[{"jsonrpc":"2.0","id":1,"result":true}]'
            client = runner.Client(None, 'root@host', args)
            client.call([runner.request(1, 'eth_syncing', [])])
            command = execute.call_args.args[0]
            socket = next(v.removeprefix('ControlPath=') for v in command if v.startswith('ControlPath='))
            self.assertLess(len(socket.encode()), 104)
            self.assertNotIn('%', socket)
            client.close()
            self.assertEqual(execute.call_args.args[0], ['ssh', '-S', socket, '-O', 'exit', 'root@host'])
            directory.cleanup.assert_called_once()

    def test_manifest_cardinality_and_ids(self):
        corpus = load_corpus()
        self.assertEqual(corpus['referenceCommit'], runner.REFERENCE)
        self.assertEqual(sum(len(d['requests']) for d in corpus['datasets']), 304)
        self.assertEqual(sum('diagnosticAllowance' in c for d in corpus['datasets'] for c in d['cases']), 11)
        for d in corpus['datasets']:
            self.assertEqual([c['id'] for c in d['cases']], [q['id'] for q in d['requests']])
            self.assertEqual(len({q['id'] for q in d['requests']}), len(d['requests']))

    def test_response_ids_reject_missing_duplicate_and_transport(self):
        rows = [runner.request(i, 'unused', []) for i in (1, 2)]
        for bad in (rows[:1], [rows[0], rows[0]], {'transportError': 'timeout'}, [{'jsonrpc': '1.0', 'id': i} for i in (1, 2)]):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                runner.index(bad, [1, 2])

    def test_materialize_regenerates_only_number_dependent_inputs(self):
        corpus = load_corpus()
        original = copy.deepcopy(corpus)
        block: dict[str, Any] = {'number': '0x1234567', 'hash': '0x' + 'a' * 64, 'transactions': [{'from': '0x' + 'b' * 40, 'nonce': '0x12'}, {}]}
        datasets = runner.materialize(corpus, block)
        self.assertEqual(corpus, original)
        state = next(d for d in datasets if d['group'] == '14-state-selection')
        for q in state['requests'][:4]:
            self.assertIn(q['params'][2]['blockOverrides']['number'], ('0x1234568', '0x1234569'))
            self.assertIn(format(int(block['number'], 16), '064x'), q['params'][2]['stateOverrides']['0x0000000000000000000000000000000000001001']['code'])
        self.assertNotIn(corpus['sourceSender']['address'], json.dumps(state))
        for d in datasets:
            for q in d['requests']:
                selector = q['params'][1]
                self.assertEqual(selector['blockHash'] if isinstance(selector, dict) else selector, block['hash'] if isinstance(selector, dict) or d['group'] == '16-native' else block['number'])
        with self.assertRaises(ValueError):
            runner.materialize(corpus, {**block, 'transactions': []})

    def test_classification_checks_kind_even_when_both_clients_wrong(self):
        case = {'id': 1, 'expectedKind': 'result'}
        error = {'id': 1, 'jsonrpc': '2.0', 'error': {'code': -32000, 'message': 'missing trie node'}}
        with self.assertRaises(ValueError):
            runner.classify(case, error, error)
        case['expectedKind'] = 'error'
        with self.assertRaises(ValueError):
            runner.classify(case, error, error)
        ok = {'id': 1, 'jsonrpc': '2.0', 'result': {'value': 42}}
        case['expectedKind'] = 'result'
        self.assertEqual(runner.classify(case, ok, ok), 'exact')
        with self.assertRaises(ValueError):
            runner.classify(case, ok, {**ok, 'result': {'value': 43}})

    def test_diagnostic_allowances_change_only_source_coordinates(self):
        corpus = load_corpus()
        for case in (c for d in corpus['datasets'] for c in d['cases'] if 'diagnosticAllowance' in c):
            allowance = case['diagnosticAllowance']
            candidate: dict[str, Any] = {'jsonrpc': '2.0', 'id': case['id'], 'error': {'code': -32000, 'message': allowance['candidateMessage']}}
            reference: dict[str, Any] = {'jsonrpc': '2.0', 'id': case['id'], 'error': {'code': -32000, 'message': allowance['referenceMessage']}}
            with self.subTest(case=case['id'], message=allowance['candidateMessage']):
                self.assertEqual(runner.classify(case, candidate, reference), 'approved_diagnostic')
                self.assertEqual(runner.classify(case, candidate, candidate), 'exact')
                for code, message in ((-32603, 'internal error'), (-32603, allowance['candidateMessage']), (-32000, 'execution failed')):
                    wrong = {**candidate, 'error': {'code': code, 'message': message}}
                    with self.subTest(code=code, message=message), self.assertRaises(ValueError):
                        runner.classify(case, wrong, wrong)
                for mutation, message in (
                    ('callback', reference['error']['message'].replace("function 'step'", "function 'fault'").replace("function 'result'", "function 'fault'")),
                    ('source', reference['error']['message'].replace('<eval>', 'other-source')),
                ):
                    bad = copy.deepcopy(reference)
                    bad['error']['message'] = message
                    with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                        runner.classify(case, candidate, bad)
                bad = copy.deepcopy(reference)
                bad['error']['message'] = re.sub(r'\b(available|offset|size|index|end) (-?\d+)',
                    lambda match: f'{match[1]} {int(match[2]) + 1}', bad['error']['message'], count=1)
                if bad != reference:
                    with self.subTest(mutation='bounds'), self.assertRaises(ValueError):
                        runner.classify(case, candidate, bad)
                reference['error']['message'] = reference['error']['message'].replace('<eval>:1:', '<eval>:99:')
                self.assertEqual(runner.classify(case, candidate, reference), 'approved_diagnostic')
                for mutation in ('code', 'message', 'data'):
                    bad = copy.deepcopy(reference)
                    bad['error'][mutation] = -32603 if mutation == 'code' else 'unexpected'
                    with self.assertRaises(ValueError):
                        runner.classify(case, candidate, bad)

    def test_expired_timeout_annotations_are_narrow_and_case_specific(self):
        messages = (
            'execution timeout',
            "execution timeout    in server-side tracer function 'step'",
            "execution timeout at step (<eval>:99:8(0))    in server-side tracer function 'step'",
            'execution timeout at Integer (bigInt:1:158(0))',
        )
        bad_messages = (
            'Error: execution timeout', 'execution timeout extra',
            'execution timeout at Integer (other:1:158(0))',
            'execution timeout at Other (bigInt:1:158(0))',
            "execution timeout    in server-side tracer function 'result'",
            "execution timeout at step (<eval>:1:8(0))    in server-side tracer function 'fault'",
        )
        for case in (c for d in load_corpus()['datasets'] for c in d['cases'] if c.get('duration') in ('-1s', '0', '1ns') and 'diagnosticAllowance' in c):
            candidate = {'jsonrpc': '2.0', 'id': case['id'], 'error': {'code': -32000, 'message': 'execution timeout'}}
            for message in messages + bad_messages:
                reference = {**candidate, 'error': {'code': -32000, 'message': message}}
                with self.subTest(case=case['id'], message=message):
                    if message in messages:
                        self.assertEqual(runner.classify(case, candidate, reference), 'exact' if message == 'execution timeout' else 'approved_diagnostic')
                    else:
                        with self.assertRaises(ValueError):
                            runner.classify(case, candidate, reference)
            other_duration = {'-1s': '0', '0': '1ns', '1ns': '-1s'}[case['duration']]
            for identifier, duration in ((21, case['duration']), (case['id'], '2s'), (case['id'], other_duration)):
                other = {**case, 'id': identifier, 'duration': duration}
                row = {**candidate, 'id': identifier}
                reference = {**row, 'error': {'code': -32000, 'message': messages[-1]}}
                with self.subTest(outside_case=identifier, duration=duration), self.assertRaises(ValueError):
                    runner.classify(other, row, reference)

    def test_shuffled_receipts_preserve_comparison(self):
        block = {'number': '0x2', 'hash': 'block', 'stateRoot': 'state', 'transactions': [
            {'hash': 'tx1', 'from': 'sender1', 'nonce': '0x0'}, {'hash': 'tx2', 'from': 'sender2', 'nonce': '0x0'}]}
        header = {'jsonrpc': '2.0', 'id': 1, 'result': block}
        receipts: list[dict[str, Any]] = [{'jsonrpc': '2.0', 'id': i, 'result': {'blockHash': 'block', 'logs': [i]}} for i in (1, 2)]
        for changed in (False, True):
            reference = copy.deepcopy(receipts[::-1])
            if changed:
                reference[0]['result']['logs'] = []
            clients = {'nethermind': Mock(), 'geth': Mock()}
            clients['nethermind'].call.side_effect = [[header], receipts]
            clients['geth'].call.side_effect = [[header], reference]
            with self.subTest(changed=changed):
                if changed:
                    with self.assertRaisesRegex(ValueError, 'Prefix receipts differ'):
                        runner.common_block(clients, {}, '0x2')
                else:
                    self.assertEqual(runner.common_block(clients, {}, '0x2')[0], block)

    def test_shuffled_metadata_checks_actual_version(self):
        before = [{'jsonrpc': '2.0', 'id': i, 'result': 'version' if i == 1 else None} for i in range(1, 6)]
        for changed in (False, True):
            after = copy.deepcopy(before[::-1])
            if changed:
                after[-1]['result'] = 'different version'
            with self.subTest(changed=changed):
                if changed:
                    with self.assertRaisesRegex(ValueError, 'Client version changed'):
                        runner.verify_versions({'node': before}, {'node': after})
                else:
                    runner.verify_versions({'node': before}, {'node': after})


if __name__ == '__main__':
    unittest.main()
