#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Generate isolated local Runner configs; does not launch nodes."""
import argparse
import json
import secrets
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--runtime-root', required=True, help='Absolute directory on the Runner host')
    parser.add_argument('--source-root', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--chain-id', type=int, default=10088288)
    parser.add_argument('--timestamp', type=int, default=int(time.time()) - 120)
    parser.add_argument('--sender', default='0x2b5ad5c4795c026514f8317c7a215e218dccd6cf')
    parser.add_argument('--bogota', action='store_true')
    parser.add_argument('--create-jwt', action='store_true', help='Generate the secret only when running on the Runner host')
    args = parser.parse_args()
    if args.timestamp <= 0 or args.chain_id <= 0 or not Path(args.runtime_root).is_absolute():
        parser.error('Positive timestamp/chain ID and absolute runtime root required')
    template = args.source_root / 'src/Nethermind/Nethermind.IntegrationTests/Resources/sepolia-amsterdam-with-test-account.json'
    chain = json.loads(template.read_text())
    chain['name'] = 'Lean proof Engine devnet'
    chain['dataDir'] = 'lean-devnet'
    chain['nodes'] = []
    params = chain['params']
    for name in list(params):
        if name.endswith('TransitionTimestamp'):
            params[name] = '0x0'
    for eip in (8141, 8288, 8250, 8272, 7906):
        params[f'eip{eip}TransitionTimestamp'] = '0x0'
    if args.bogota:
        params['eip7805TransitionTimestamp'] = '0x0'
    params.update(networkId=hex(args.chain_id), chainId=hex(args.chain_id), terminalTotalDifficulty='0x0',
                  beaconChainGenesisTimestamp=hex(args.timestamp))
    params['blobSchedule'] = [dict(params['blobSchedule'][0], timestamp='0x0')]
    chain['genesis'].update(timestamp=hex(args.timestamp), difficulty='0x0', slotNumber='0x0',
                            extraData='0x', baseFeePerGas='0x3b9aca00', gasLimit=hex(30_000_000),
                            blobGasUsed='0x0', excessBlobGas='0x0')
    chain['genesis']['seal']['ethereum']['nonce'] = '0x0000000000000000'
    chain['accounts'] = {address: account for address, account in chain['accounts'].items() if account.get('code')}
    chain['accounts'][args.sender] = {'balance': str(100 * 10**18), 'nonce': '0', 'code': '0x'}
    args.out.mkdir(parents=True, exist_ok=True)
    write = lambda name, value: (args.out / name).write_text(json.dumps(value, indent=2) + '\n')
    write('chain.json', chain)
    secret = args.out / 'jwt.hex'
    if args.create_jwt and not secret.exists():
        secret.write_text(secrets.token_hex(32) + '\n')
        secret.chmod(0o600)
    root = args.runtime_root.rstrip('/')
    for node, rpc, engine, p2p in ((1, 19145, 19151, 19303), (2, 19245, 19251, 19304)):
        directory = f'{root}/node{node}'
        write(f'node{node}.json', {
            'Init': {'ChainSpecPath': f'{root}/chain.json', 'BaseDbPath': f'{directory}/db',
                     'DataDir': directory, 'DiscoveryEnabled': False, 'PeerManagerEnabled': True,
                     'ProcessingEnabled': True, 'WebSocketsEnabled': False},
            'KeyStore': {'KeyStoreDirectory': f'{directory}/keystore'},
            'Network': {'LocalIp': '127.0.0.1', 'ExternalIp': '127.0.0.1',
                        'EnableExternalIpResolution': False, 'P2PPort': p2p, 'DiscoveryPort': p2p,
                        'OnlyStaticPeers': True, 'MaxActivePeers': 2},
            'Sync': {'NetworkingEnabled': True, 'FastSync': False, 'SnapSync': False},
            'JsonRpc': {'Enabled': True, 'Host': '127.0.0.1', 'Port': rpc,
                        'EngineHost': '127.0.0.1', 'EnginePort': engine,
                        'JwtSecretFile': f'{root}/jwt.hex', 'Timeout': 180000,
                        'MaxRequestBodySize': 64 * 1024 * 1024,
                        'EnabledModules': ['Admin', 'Eth', 'Net', 'TxPool', 'Web3'],
                        'EngineEnabledModules': ['Engine'], 'MaxLoggedRequestParametersCharacters': 128},
            'Merge': {'Enabled': True, 'TerminalTotalDifficulty': '0',
                      'NewPayloadBlockProcessingTimeout': 120000},
            'Mining': {'Enabled': False},
            'Blocks': {'TargetBlockGasLimit': 30_000_000, 'SecondsPerSlot': 12,
                       'BlockProductionTimeoutMs': 120000, 'GenesisTimeoutMs': 120000}
        })
    print(f'Generated {args.out}; independent databases/identities and shared genesis; JWT configured on Runner host')


if __name__ == '__main__':
    main()
