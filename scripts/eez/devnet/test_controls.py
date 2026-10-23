import copy
import contextlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

import controls
from embedded import CASES
from test_canonical_snapshot import Node


def hash_value(number):
    return '0x' + f'{number:064x}'


class Source:
    def __init__(self, height=6):
        self.height = height
        self.blocks = {hash_value(number): {
            'number': hex(number), 'hash': hash_value(number), 'parentHash': hash_value(number - 1),
            'stateRoot': hash_value(number + 100), 'transactions': [],
        } for number in range(1, height + 1)}
        self.witness = {field: ['0x01'] for field in ('state', 'codes', 'keys', 'headers')}
        self.calls = []

    def call(self, method, *params):
        self.calls.append((method, params))
        if method == 'eth_getBlockByHash': return self.blocks.get(params[0])
        if method == 'debug_getRawBlock': return '0x010203'
        if method == 'debug_executionWitnessByBlockHash': return copy.deepcopy(self.witness)
        raise AssertionError('unexpected RPC: ' + method)


class StreamingControlInputTests(unittest.TestCase):
    def test_capture_uses_the_settled_ancestry_and_exact_raw_witness_bytes(self):
        source = Source()
        data = controls.capture(source, {'endpoint': hash_value(6), 'height': 6}, 7)
        self.assertEqual(data['anchor_number'], 4)
        self.assertEqual(data['anchor_hash'], hash_value(4))
        self.assertEqual([block['number'] for block in data['blocks']], [5, 6])
        for block in data['blocks']:
            self.assertEqual(block['witness'], source.witness)
            self.assertEqual(block['rlp'], '0x010203')
        self.assertEqual([params for method, params in source.calls if method == 'debug_executionWitnessByBlockHash'],
                         [(hash_value(5),), (hash_value(6),)])

    def test_capture_rejects_malformed_or_missing_execution_inputs(self):
        for failure in ('identity', 'height', 'state-root', 'missing-witness', 'empty-state', 'bad-hex'):
            with self.subTest(failure=failure):
                source = Source()
                if failure == 'identity': source.blocks[hash_value(6)]['hash'] = hash_value(9)
                if failure == 'height': source.blocks[hash_value(6)]['number'] = '0x7'
                if failure == 'state-root': source.blocks[hash_value(6)]['stateRoot'] = '0x01'
                if failure == 'missing-witness': del source.witness['keys']
                if failure == 'empty-state': source.witness['state'] = []
                if failure == 'bad-hex': source.witness['codes'] = ['0xnotbytes']
                with self.assertRaises((AssertionError, ValueError)):
                    controls.capture(source, {'endpoint': hash_value(6), 'height': 6}, 7)

    def test_missing_transaction_empty_pair_fails_after_bounded_ancestry_walk(self):
        source = Source(68)
        for block in source.blocks.values(): block['transactions'] = [hash_value(200)]
        with self.assertRaisesRegex(AssertionError, 'no consecutive'):
            controls.capture(source, {'endpoint': hash_value(68), 'height': 68}, 7)
        self.assertEqual(len(source.calls), 64)

    def test_transaction_blocks_break_the_consecutive_empty_pair(self):
        source = Source()
        source.blocks[hash_value(5)]['transactions'] = [hash_value(200)]
        data = controls.capture(source, {'endpoint': hash_value(6), 'height': 6}, 7)
        self.assertEqual([block['number'] for block in data['blocks']], [3, 4])

    def test_report_cannot_pass_with_missing_controls_or_changed_execution_oracles(self):
        data = controls.capture(Source(), {'endpoint': hash_value(6), 'height': 6}, 7)
        good = {'result': 'pass', 'anchor_hash': data['anchor_hash'],
                'blocks': [{key: block[key] for key in ('number', 'hash', 'state_root')} for block in data['blocks']],
                'checks': ['disconnect-resume', 'rewind-reexecute', 'pipelined-cancel', 'cancelled-resume-rejected', 'independent-session']}
        controls.validate_report(good, data)
        for failure in ('result', 'anchor', 'root', 'missing-block', 'missing-control'):
            with self.subTest(failure=failure):
                report = copy.deepcopy(good)
                if failure == 'result': report['result'] = 'fail'
                if failure == 'anchor': report['anchor_hash'] = hash_value(999)
                if failure == 'root': report['blocks'][0]['state_root'] = hash_value(999)
                if failure == 'missing-block': report['blocks'].pop()
                if failure == 'missing-control': report['checks'].pop()
                with self.assertRaises(AssertionError): controls.validate_report(report, data)

    def test_producer_compiles_rust_targets_and_consumers_execute_precompiled_binaries(self):
        root = Path(__file__).resolve().parents[3]
        producer = (root / 'scripts/eez/devnet/build-runtime.sh').read_text()
        self.assertEqual(producer.count('cargo build --locked --profile ci-e2e'), 1)
        for suite in (*CASES, 'nethermind_streaming_controls'):
            self.assertIn('--test ' + suite, producer)
        self.assertLess(producer.index('controls.py" install'), producer.index('cargo build'))
        self.assertLess(producer.index('cargo build'), producer.index('bundle.py" tests'))
        fixtures = (root / 'scripts/eez/devnet/build-fixtures.sh').read_text()
        self.assertNotIn('cargo ', fixtures)
        for name in ('run.sh', 'observe.sh', 'embedded.sh', 'test-partition.sh'):
            consumer = (root / 'scripts/eez/devnet' / name).read_text()
            self.assertNotIn('cargo test', consumer)
            self.assertNotIn('cargo build', consumer)
        workflow = (root / '.github/workflows/eez-devnet-tests.yml').read_text()
        preparation = (root / '.github/workflows/eez-devnet-prepare.yml').read_text()
        consumer = (root / '.github/workflows/eez-devnet-profile.yml').read_text()
        self.assertLess(preparation.index('Install upstream integration test toolchain'),
                        preparation.index('Build matching runtime and test executables'))
        for profile in ('rollup0', 'rollupx'):
            self.assertIn('needs: [candidate, prepare-' + profile + ']', workflow)
        self.assertIn("timeout-minutes: ${{ inputs.profile == 'rollupx' && matrix.suite == 'protocol' && 20 || 15 }}", consumer)
        self.assertIn('fromJSON(inputs.suites)', consumer)

    def invoke(self, directory, mode, context, failure):
        directory = Path(directory)
        context = copy.deepcopy(context)
        (directory / 'fresh-follower.json').write_text(json.dumps(context))
        nodes = {role: Node(role) for role in ('l1', 'reference')}
        nodes['reference'].height = 6
        nodes['reference'].canonical_hash = hash_value(6)
        source = Source()
        def source_call(method, *params):
            if method in ('eth_getBlockByNumber', 'eth_chainId'):
                return nodes['reference'].call(method, *params)
            return source.call(method, *params)
        rpc = {'l1': nodes['l1'], 'reference': Mock(call=Mock(side_effect=source_call))}
        def mutate():
            if failure.endswith('l1-reorg'): nodes['l1'].l1_hash = 'fork'
            if failure.endswith('l2-reorg'): nodes['reference'].canonical_hash = 'fork'
            if failure == 'genesis': nodes['reference'].genesis_hash = 'other-genesis'
            if failure == 'chain': nodes['reference'].chain_id = '0x3'
        original = controls.capture
        def capture(*args):
            result = original(*args)
            if failure.startswith('after-'): mutate()
            return result
        original_report = controls.validate_report
        def validate_report(*args):
            original_report(*args)
            if failure.startswith('after-'): mutate()
        if not failure.startswith('after-'): mutate()
        environment = {'EEZ_UPSTREAM': str(directory), 'EEZ_CI_RESULT_DIR': str(directory),
                       'EEZ_DEVNET_L1_RPC': 'l1', 'EEZ_DEVNET_L2_RPC': 'reference', 'EEZ_ROLLUP_ID': '7',
                       'GITHUB_SHA': 'candidate', 'EEZ_CONTROL_IMAGE_ID': 'image'}
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.dict(os.environ, environment))
            stack.enter_context(patch.object(sys, 'argv', ['controls', mode]))
            stack.enter_context(patch.object(controls, 'Rpc', side_effect=lambda url, deadline: rpc[url]))
            stack.enter_context(patch.object(controls, 'capture', side_effect=capture))
            stack.enter_context(patch.object(controls, 'validate_report', side_effect=validate_report))
            controls.main()

    def test_entry_point_rejects_failed_or_wrong_profile_observations_and_canonical_changes(self):
        context = {'result': 'pass', 'phase': 'fresh-follower', 'profile': 'legacy-rollup0-0x76',
                   'height': 6, 'endpoint': hash_value(6), 'canonical_l1_number': '0x1', 'canonical_l1_hash': 'snapshot',
                   'genesis_state': {'hash': 'genesis', 'chain_id': 2}}
        for failure in ('', 'failed', 'phase', 'profile', 'l1-reorg', 'l2-reorg', 'after-l1-reorg', 'after-l2-reorg', 'genesis', 'chain'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                observation = copy.deepcopy(context)
                if failure == 'failed': observation['result'] = 'fail'
                if failure == 'phase': observation['phase'] = 'after-restart'
                if failure == 'profile': observation['profile'] = 'rollupx-native'
                output = Path(directory) / 'streaming-controls-input.json'
                if failure:
                    with self.assertRaises(AssertionError): self.invoke(directory, 'capture', observation, failure)
                    self.assertFalse(output.exists())
                else:
                    self.invoke(directory, 'capture', observation, failure)
                    self.assertEqual(json.loads(output.read_text())['context'], context)

    def test_report_requires_the_original_context_and_final_canonical_recheck(self):
        context = {'result': 'pass', 'phase': 'fresh-follower', 'profile': 'legacy-rollup0-0x76',
                   'height': 6, 'endpoint': hash_value(6), 'canonical_l1_number': '0x1', 'canonical_l1_hash': 'snapshot',
                   'genesis_state': {'hash': 'genesis', 'chain_id': 2}}
        for failure in ('', 'context', 'l1-reorg', 'l2-reorg', 'after-l1-reorg', 'after-l2-reorg'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                directory = Path(directory)
                self.invoke(directory, 'capture', context, '')
                data = json.loads((directory / 'streaming-controls-input.json').read_text())
                report = {'result': 'pass', 'anchor_hash': data['anchor_hash'],
                          'blocks': [{key: block[key] for key in ('number', 'hash', 'state_root')} for block in data['blocks']],
                          'checks': ['disconnect-resume', 'rewind-reexecute', 'pipelined-cancel', 'cancelled-resume-rejected', 'independent-session']}
                (directory / 'streaming-controls-probe.json').write_text(json.dumps(report))
                (directory / 'upstream.json').write_text(json.dumps({'revision': 'upstream'}))
                if failure == 'context':
                    data['context']['canonical_l1_hash'] = 'fork'
                    (directory / 'streaming-controls-input.json').write_text(json.dumps(data))
                output = directory / 'streaming-controls.json'
                if failure:
                    with self.assertRaises(AssertionError): self.invoke(directory, 'report', context, failure)
                    self.assertFalse(output.exists())
                else:
                    self.invoke(directory, 'report', context, '')
                    self.assertEqual(json.loads(output.read_text())['wire'], 'prove.v2')
