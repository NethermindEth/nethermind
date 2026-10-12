#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Small authenticated Engine API driver for two independent Runner processes."""
import argparse
import base64
import hashlib
import hmac
import json
import math
import shlex
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

ZERO_HASH = '0x' + '00' * 32


class RpcError(RuntimeError):
    def __init__(self, method, error):
        super().__init__(f'{method}: {error}')
        self.error = error


class Rpc:
    def __init__(self, url, secret=None, timeout=180):
        self.url, self.secret, self.timeout, self.sequence = url, secret, timeout, 0

    def call(self, method, params):
        self.sequence += 1
        request = {'jsonrpc': '2.0', 'id': self.sequence, 'method': method, 'params': params}
        headers = {'Content-Type': 'application/json'}
        if self.secret is not None:
            encode = lambda value: base64.urlsafe_b64encode(value).rstrip(b'=').decode()
            token = encode(b'{"alg":"HS256","typ":"JWT"}') + '.' + encode(json.dumps({'iat': int(time.time())}).encode())
            headers['Authorization'] = 'Bearer ' + token + '.' + encode(hmac.new(self.secret, token.encode(), hashlib.sha256).digest())
        req = urllib.request.Request(self.url, json.dumps(request).encode(), headers)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as response:
                body = json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(f'{method}: HTTP {error.code}') from error
        if 'error' in body:
            raise RpcError(method, body['error'])
        if body.get('id') != self.sequence or 'result' not in body:
            raise RuntimeError(f'{method}: malformed JSON-RPC response')
        return body['result']


def quantity(value):
    return int(value, 16) if isinstance(value, str) and value.startswith('0x') else int(value)


def wait_for(action, predicate, timeout, description):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            last = action()
        except (urllib.error.URLError, ConnectionError, TimeoutError) as error:
            last = str(error)
            time.sleep(0.25)
            continue
        if predicate(last):
            return last
        time.sleep(0.25)
    raise RuntimeError(f'Timed out: {description}; last={last}')


def valid(result):
    status = result.get('payloadStatus', result)
    if status.get('status') != 'VALID':
        raise RuntimeError(f'Expected VALID, got {status}')
    return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--jwt', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    parser.add_argument('--rpc1', default='http://127.0.0.1:19145')
    parser.add_argument('--rpc2', default='http://127.0.0.1:19245')
    parser.add_argument('--engine1', default='http://127.0.0.1:19151')
    parser.add_argument('--engine2', default='http://127.0.0.1:19251')
    parser.add_argument('--bogota', action='store_true')
    parser.add_argument('--timeout', type=float, default=180)
    parser.add_argument('--build-wait', type=float, default=10)
    parser.add_argument('--gas-limit', type=int, default=30_000_000)
    parser.add_argument('--mutator', help='Command for the LeanBench offline canonical payload mutator')
    parser.add_argument('--limit', type=int, help='Run only the first N manifest requests')
    parser.add_argument('--source-revision', help='Exact Runner source commit for the evidence report')
    parser.add_argument('--transactions-per-block', type=int, default=1)
    parser.add_argument('--resume', action='store_true', help='Continue on an identical existing head with matching sender nonce')
    args = parser.parse_args()
    if not math.isfinite(args.timeout) or not math.isfinite(args.build_wait) or args.timeout <= 0 or args.build_wait < 0 or args.gas_limit <= 0 or (args.limit is not None and args.limit <= 0):
        parser.error('Invalid timeout/build-wait/gas-limit/request limit')
    if not 1 <= args.transactions_per_block <= 128:
        parser.error('Transactions per block must be between 1 and 128')
    manifest = json.loads(args.manifest.read_text())
    if not manifest.get('requests'):
        parser.error('Manifest must contain at least one signed request')
    secret = bytes.fromhex(args.jwt.read_text().strip().removeprefix('0x'))
    if len(secret) != 32:
        parser.error('JWT secret must contain exactly 32 bytes')
    rpc = [Rpc(args.rpc1, timeout=args.timeout), Rpc(args.rpc2, timeout=args.timeout)]
    engines = [Rpc(args.engine1, secret, args.timeout), Rpc(args.engine2, secret, args.timeout)]
    fcu = 'engine_forkchoiceUpdatedV5' if args.bogota else 'engine_forkchoiceUpdatedV4'
    new_payload = 'engine_newPayloadV6' if args.bogota else 'engine_newPayloadV5'
    args.out.mkdir(parents=True, exist_ok=True)
    report = {'sourceManifest': str(args.manifest), 'manifestSha256': hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              'driverSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), 'sourceRevision': args.source_revision,
              'chainId': manifest['chainId'], 'completed': False,
              'blocks': [], 'negativeIngress': [], 'engineVersions': {'forkchoice': fcu, 'getPayload': 'engine_getPayloadV6', 'newPayload': new_payload},
              'negativeBlockProofChecks': 'canonical hash recomputed' if args.mutator else 'not run (no offline mutator supplied)'}

    def save():
        temporary = args.out / 'report.tmp'
        temporary.write_text(json.dumps(report, indent=2) + '\n')
        temporary.replace(args.out / 'report.json')

    def write(name, value):
        path = args.out / name
        path.write_text(json.dumps(value, indent=2) + '\n')
        return path

    def state(head, safe):
        return {'headBlockHash': head, 'safeBlockHash': safe, 'finalizedBlockHash': safe}

    try:
        for endpoint in rpc:
            wait_for(lambda: endpoint.call('eth_getBlockByNumber', ['latest', False]), bool, args.timeout, 'Runner genesis')
        chain_ids = [quantity(endpoint.call('eth_chainId', [])) for endpoint in rpc]
        if chain_ids != [manifest['chainId']] * 2:
            raise RuntimeError(f'Chain IDs differ from manifest: {chain_ids}')
        genesis = [endpoint.call('eth_getBlockByNumber', ['0x0', False]) for endpoint in rpc]
        heads = [endpoint.call('eth_getBlockByNumber', ['latest', False]) for endpoint in rpc]
        if genesis[0]['hash'] != genesis[1]['hash'] or heads[0]['hash'] != heads[1]['hash']:
            raise RuntimeError('Nodes need identical genesis and starting head')
        if quantity(heads[0]['number']) != 0 and not args.resume:
            raise RuntimeError('Use fresh databases or --resume with a matching nonce manifest')
        nonces = [quantity(endpoint.call('eth_getTransactionCount', [manifest['sender'], 'latest'])) for endpoint in rpc]
        if nonces != [manifest['firstNonce']] * 2:
            raise RuntimeError(f'Sender nonce differs from manifest: {nonces}')
        report.update(genesisHash=genesis[0]['hash'], nodeVersions=[endpoint.call('web3_clientVersion', []) for endpoint in rpc])
        info = [endpoint.call('admin_nodeInfo', []) for endpoint in rpc]
        for index in range(2):
            added = rpc[index].call('admin_addPeer', [info[1-index]['enode'], True])
            if not added:
                raise RuntimeError('admin_addPeer declined the static peer')
        peers = []
        for endpoint in rpc:
            peers.append(wait_for(lambda: endpoint.call('admin_peers', []),
                                  lambda items: any('lean/1' in peer.get('caps', []) for peer in items),
                                  args.timeout, 'negotiated lean/1 peer'))
        report['peers'] = [[{'id': peer.get('id'), 'caps': peer.get('caps')} for peer in items] for items in peers]
        head = heads[0]
        safe = genesis[0]['hash']
        for engine in engines:
            valid(engine.call(fcu, [state(head['hash'], safe), None]))
        save()
        requests = manifest['requests'][:args.limit] if args.limit else manifest['requests']
        batches = [requests[start:start + args.transactions_per_block] for start in range(0, len(requests), args.transactions_per_block)]
        for position, batch in enumerate(batches):
            producer = position % 2
            ingress = 1 - producer
            batch_name = batch[0]['name'] if len(batch) == 1 else f"{batch[0]['name']}..{batch[-1]['name']}"
            tx_hashes = [entry['transactionHash'] for entry in batch]
            expected_dependencies = {(dep['scheme'], dep['dataHash'], dep['verificationKey']) for entry in batch for dep in entry['dependencies']}
            expected_signatures = sum(int(dep[0], 16) == 0x10 for dep in expected_dependencies)
            expected_starks = sum(int(dep[0], 16) == 0x11 for dep in expected_dependencies)
            if expected_starks > 1:
                raise RuntimeError('This devnet load plan permits at most one generic STARK per block')
            gossip_seconds = 0
            for entry in batch:
                tx_hash = entry['transactionHash']
                negative = json.loads((args.manifest.parent / entry['negativeRequestFile']).read_text())
                for index, endpoint in enumerate(rpc):
                    known_before = endpoint.call('eth_getTransactionByHash', [tx_hash])
                    if known_before is not None and not args.resume:
                        raise RuntimeError('Transaction already exists; use --resume to retry a prior verified admission')
                    try:
                        endpoint.call(negative['method'], negative['params'])
                    except RpcError as rejected:
                        passed = rejected.error.get('code') == -32000 and rejected.error.get('message') == 'a wrapper dependency proof failed verification'
                        report['negativeIngress'].append({'name': entry['name'], 'node': index+1, 'rejected': True,
                                                          'expected': 'proof verification rejection', 'passed': passed, 'error': rejected.error})
                        if not passed:
                            raise RuntimeError(f'Tampered wrapper returned an unexpected error on node {index+1}: {rejected.error}')
                    else:
                        raise RuntimeError(f'Tampered witness accepted by node {index+1}')
                    known_after = endpoint.call('eth_getTransactionByHash', [tx_hash])
                    if known_before is None and known_after is not None:
                        raise RuntimeError('Rejected wrapper leaked transaction into pool')
                request = json.loads((args.manifest.parent / entry['requestFile']).read_text())
                accepted = rpc[ingress].call(request['method'], request['params'])
                if tx_hash.lower() not in [value.lower() for value in accepted]:
                    raise RuntimeError(f'Wrapper did not admit expected transaction: {accepted}')
                gossip_start = time.monotonic()
                wait_for(lambda: rpc[producer].call('eth_getTransactionByHash', [tx_hash]),
                         lambda tx: tx is not None and tx.get('blockHash') is None, args.timeout, 'peer-gossiped pending transaction')
                gossip_seconds += time.monotonic() - gossip_start
            attrs = {'timestamp': hex(quantity(head['timestamp']) + 12), 'prevRandao': ZERO_HASH,
                     'suggestedFeeRecipient': manifest['sender'], 'withdrawals': [], 'parentBeaconBlockRoot': ZERO_HASH,
                     'slotNumber': hex(quantity(head.get('slotNumber') or 0) + 1), 'targetGasLimit': hex(args.gas_limit)}
            if args.bogota:
                attrs['inclusionListTransactions'] = []
            started = time.monotonic()
            prepared = engines[producer].call(fcu, [state(head['hash'], safe), attrs])
            valid(prepared)
            payload_id = prepared.get('payloadId')
            if not payload_id:
                raise RuntimeError('No payload ID returned')
            report['currentBuild'] = {'payloadId': payload_id, 'producer': producer+1, 'parentBlockHash': head['hash'],
                                      'expectedTransactions': len(batch), 'expectedTransactionHashes': tx_hashes}
            write(f'forkchoice-{position:03d}.json', {'method': fcu, 'params': [state(head['hash'], safe), attrs], 'result': prepared})
            save()
            time.sleep(args.build_wait)
            result = engines[producer].call('engine_getPayloadV6', [payload_id])
            write(f'get-payload-{position:03d}.json', result)
            payload = result['executionPayload']
            report['currentBuild'].update(blockHash=payload['blockHash'], actualTransactions=len(payload['transactions']))
            if {entry['rawTransaction'].lower() for entry in batch} != {tx.lower() for tx in payload['transactions']} or len(payload['transactions']) != len(batch):
                raise RuntimeError(f"Built block transaction set/count differs: expected {len(batch)}, got {len(payload['transactions'])}; getPayload response saved")
            proof = payload.get('recursiveStarkProof')
            deps_hash = payload.get('recursiveStarkBlockDepsHash')
            if not proof or proof == '0x' or not deps_hash or len(deps_hash) != 66:
                raise RuntimeError('Built block lacks recursive proof/commitment')
            build_seconds = time.monotonic() - started
            params = [payload, [], attrs['parentBeaconBlockRoot'], result.get('executionRequests', [])]
            if args.bogota:
                params.append([])
            payload_request = {'jsonrpc': '2.0', 'id': 1, 'method': new_payload, 'params': params}
            input_path = write(f'payload-{position:03d}.json', payload_request)
            negative_statuses = []
            native_inspection = None
            if args.mutator:
                for mutation in ('proof', 'commitment'):
                    output = args.out / f'payload-{position:03d}-invalid-{mutation}.json'
                    subprocess.run(shlex.split(args.mutator) + [f'--devnet-payload={input_path}', f'--mutation={mutation}', f'--out={output}'], check=True)
                    changed = json.loads(output.read_text())
                    inspection = json.loads(Path(str(output) + '.inspection.json').read_text())
                    if not inspection['canonicalHeaderHashMatches'] or not inspection['originalNativeProofValid'] or inspection['resultingNativeProofValid']:
                        raise RuntimeError('Offline native inspection did not authenticate the original and reject the mutation')
                    native_inspection = {key: inspection[key] for key in ('signatures', 'starks', 'proofBytes', 'originalNativeProofValid')}
                    if (inspection['signatures'], inspection['starks']) != (expected_signatures, expected_starks):
                        raise RuntimeError(f'Native verified dependency counts differ from manifest: {native_inspection}')
                    if {value.lower() for value in inspection['transactionHashes']} != {value.lower() for value in tx_hashes}:
                        raise RuntimeError('Native decoded transaction hashes differ from manifest')
                    if changed['params'][0]['blockHash'] == payload['blockHash']:
                        raise RuntimeError('Mutator did not recompute changed header hash')
                    verdict = engines[ingress].call(changed['method'], changed['params'])
                    expected_error = 'InvalidRecursiveStark:' if mutation == 'proof' else 'InvalidBlockDepsHash:'
                    passed = verdict.get('status') == 'INVALID' and (verdict.get('validationError') or '').startswith(expected_error)
                    negative_statuses.append({'mutation': mutation, 'status': verdict.get('status'),
                                              'expected': expected_error[:-1], 'passed': passed, 'validationError': verdict.get('validationError')})
                    if not passed:
                        raise RuntimeError(f'Invalid {mutation} block returned {verdict}')
            imported = time.monotonic()
            for engine in engines:
                status = valid(engine.call(new_payload, params))
                if status.get('latestValidHash') != payload['blockHash']:
                    raise RuntimeError(f'Wrong latest valid hash: {status}')
                valid(engine.call(fcu, [state(payload['blockHash'], safe), None]))
            import_seconds = time.monotonic() - imported
            receipts = []
            for endpoint in rpc:
                node_receipts = []
                for tx_hash in tx_hashes:
                    receipt = wait_for(lambda: endpoint.call('eth_getTransactionReceipt', [tx_hash]), bool, args.timeout, 'receipt after import')
                    if receipt['blockHash'] != payload['blockHash'] or quantity(receipt['status']) != 1:
                        raise RuntimeError(f'Unexpected receipt: {receipt}')
                    node_receipts.append(receipt)
                receipts.append(node_receipts)
            write(f'receipts-{position:03d}.json', receipts)
            current = [endpoint.call('eth_getBlockByNumber', ['latest', False]) for endpoint in rpc]
            if any(block['hash'] != payload['blockHash'] for block in current):
                raise RuntimeError('Independent heads diverged after forkchoice')
            report['blocks'].append({'name': batch_name, 'producer': producer+1, 'ingress': ingress+1,
                                     'transactionHash': tx_hashes[0], 'transactionHashes': tx_hashes, 'transactionCount': len(batch), 'blockHash': payload['blockHash'], 'blockNumber': payload['blockNumber'],
                                     'proofBytes': (len(proof)-2)//2, 'dependencyHash': deps_hash, 'receiptStatus': receipts[0][0]['status'],
                                     'gossipWaitSeconds': gossip_seconds, 'buildSeconds': build_seconds, 'importSeconds': import_seconds,
                                     'negativePayloadStatus': negative_statuses, 'nativeInspection': native_inspection})
            head = current[0]
            report.pop('currentBuild', None)
            save()
            print(f"{batch_name}: {len(batch)} tx; node{producer+1} built {payload['blockNumber']}, {report['blocks'][-1]['proofBytes']} proof bytes; both receipts/head agree", flush=True)
        stale_attrs = dict(attrs, timestamp=head['timestamp'])
        try:
            stale = engines[0].call(fcu, [state(head['hash'], safe), stale_attrs])
        except RpcError as rejected:
            passed = rejected.error.get('code') == -38003 and rejected.error.get('message', '').startswith('Invalid payload timestamp ')
            report['staleAttributes'] = {'rejected': True, 'expected': 'invalid timestamp attributes', 'passed': passed, 'error': rejected.error}
            if not passed:
                raise RuntimeError(f'Stale attributes returned an unexpected error: {rejected.error}')
        else:
            report['staleAttributes'] = {'rejected': False, 'status': stale.get('payloadStatus', {}).get('status'),
                                         'expected': 'invalid timestamp attributes', 'passed': False}
            raise RuntimeError(f'Stale attributes did not return the expected invalid-timestamp RPC error: {stale}')
        if any(endpoint.call('eth_getBlockByNumber', ['latest', False])['hash'] != head['hash'] for endpoint in rpc):
            raise RuntimeError('Stale attributes changed canonical head')
        report['completed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        save()


if __name__ == '__main__':
    main()
