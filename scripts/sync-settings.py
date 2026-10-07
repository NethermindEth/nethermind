# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import json
import emoji
import requests
import sys
import time

CONFIGS_PATH = './src/Nethermind/Nethermind.Runner/configs'
APPLICATION_JSON = { 'Content-type': 'application/json' }
REQUEST_TIMEOUT_SECONDS = 30
ATTEMPTS = 5
SUPERCHAIN_CHAINS = ["op-mainnet", "op-sepolia", "worldchain-mainnet", "worldchain-sepolia"]
# Configs that must keep another config's pivot: they take the pivot it was just given instead of fetching their own,
# so a block landing between two fetches can't leave them on different pivots.
PIVOT_FOLLOWERS = {"mainnet": ["mainnet_aztec"]}

configs = {
    # fast sync section
    "mainnet": {
        "url": "https://api.etherscan.io/v2/api?chainid=1",
        "blockReduced": 1000,
        "multiplierRequirement": 1000,
        "isPoS": True
    },
    "gnosis": {
        "url": "https://rpc.gnosischain.com",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "chiado": {
        "url": "https://rpc.chiadochain.net",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "sepolia": {
        "url": "https://api.etherscan.io/v2/api?chainid=11155111",
        "blockReduced": 1000,
        "multiplierRequirement": 1000,
        "isPoS": True
    },
    "op-mainnet": {
        "url": "https://mainnet.optimism.io",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "op-sepolia": {
        "url": "https://sepolia.optimism.io",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "worldchain-mainnet": {
        "url": "https://worldchain-mainnet.g.alchemy.com/public",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "worldchain-sepolia": {
        "url": "https://worldchain-sepolia.g.alchemy.com/public",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "xdc": {
        "url": "https://erpc.xinfin.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "xdc-testnet": {
        "url": "https://rpc.apothem.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    }
}

def fastBlocksSettings(configuration, apiUrl, blockReduced, multiplierRequirement, isPoS):
    if "etherscan" in apiUrl:
        params = {
            'module': 'proxy',
            'action': 'eth_blockNumber',
            'apikey': key,
        }
        response = requests.get(apiUrl, params=params, timeout=REQUEST_TIMEOUT_SECONDS)
    else:
        data_req = '{"id":0,"jsonrpc":"2.0","method": "eth_blockNumber","params": []}'
        response = requests.post(apiUrl, headers=APPLICATION_JSON, data=data_req, timeout=REQUEST_TIMEOUT_SECONDS)
    latestBlock = int(json.loads(response.text)['result'], 16)

    baseBlock = latestBlock - blockReduced
    baseBlock = baseBlock - baseBlock % multiplierRequirement

    if "etherscan" in apiUrl:
        params = {
            'module': 'proxy',
            'action': 'eth_getBlockByNumber',
            'tag': f'{hex(baseBlock)}',
            'boolean': 'true',
            'apikey': key,
        }
        response = requests.get(apiUrl, params=params, timeout=REQUEST_TIMEOUT_SECONDS)
    else:
        data_req = f'{{"id":0,"jsonrpc":"2.0","method": "eth_getBlockByNumber","params": ["{hex(baseBlock)}", false]}}'
        response = requests.post(apiUrl, headers=APPLICATION_JSON, data=data_req, timeout=REQUEST_TIMEOUT_SECONDS)
    pivot = json.loads(response.text)

    pivotHash = pivot['result']['hash']
    pivotTotalDifficulty = int(pivot['result'].get('totalDifficulty', '0x0'), 16)

    print(configuration + ' LatestBlock: ' + str(latestBlock))
    return baseBlock, pivotHash, pivotTotalDifficulty

def writePivot(configurations, baseBlock, pivotHash, pivotTotalDifficulty, isPoS):
    # Load every target before writing any, so a broken follower config can't leave its leader on a different pivot.
    updated = {}
    for configuration in configurations:
        print(configuration + ' PivotNumber: ' + str(baseBlock))
        print(configuration + ' PivotHash: ' + str(pivotHash))
        if not isPoS:
          print(configuration + ' PivotTotalDifficulty: ' + str(pivotTotalDifficulty))

        with open(f'{CONFIGS_PATH}/{configuration}.json', 'r') as mainnetCfg:
            data = json.load(mainnetCfg)

        data['Sync']['PivotNumber'] = baseBlock
        data['Sync']['PivotHash'] = pivotHash

        if not isPoS:
            data['Sync']['PivotTotalDifficulty'] = str(pivotTotalDifficulty)

        updated[configuration] = data

    for configuration, data in updated.items():
        with open(f'{CONFIGS_PATH}/{configuration}.json', 'w') as mainnetCfgChanged:
            json.dump(data, mainnetCfgChanged, indent=2)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Fast Sync configuration settings")
    parser.add_argument("-k", "--key", default="", help="etherscan API key")
    parser.add_argument("--superchain", action="store_true", help="only process superchain chains")

    args = parser.parse_args()
    key = args.key

    print(emoji.emojize("Fast Sync configuration settings initialization     :white_check_mark: "))
    failed = []
    for config, value in configs.items():
        if args.superchain and config not in SUPERCHAIN_CHAINS:
            continue

        print(emoji.emojize(f"{config.capitalize()} section                                     :white_check_mark: "))
        # Public RPCs intermittently time out or answer with a non-JSON body; one flaky endpoint must not block the others.
        for attempt in range(1, ATTEMPTS + 1):
            try:
                pivot = fastBlocksSettings(config, value['url'], value['blockReduced'], value['multiplierRequirement'], value['isPoS'])
                break
            except (requests.RequestException, ValueError, KeyError, TypeError) as e:
                print(f"{config} attempt {attempt}/{ATTEMPTS} failed: {type(e).__name__}")
                if attempt < ATTEMPTS:
                    time.sleep(10 * attempt)
        else:
            print(f"::error::{config}: could not fetch the pivot from {value['url']}, config left unchanged")
            failed.append(config)
            continue

        writePivot([config, *PIVOT_FOLLOWERS.get(config, [])], *pivot, value['isPoS'])

    if failed:
        sys.exit(f"Failed to update: {', '.join(failed)}")
