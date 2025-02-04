# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import subprocess
import emoji
import sys
import requests

configsPath = './src/Nethermind/Nethermind.Runner/configs'

key = sys.argv[1]

headers = {
    'Content-type': 'application/json',
}

print(emoji.emojize("Fast Sync configuration settings initialization     :white_check_mark: "))

configs = {
    # fast sync section
    "mainnet": {
        "url": "api.etherscan.io",
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
        "url": "api-sepolia.etherscan.io",
        "blockReduced": 1000,
        "multiplierRequirement": 1000,
        "isPoS": True
    },
    "energyweb": {
        "url": "https://rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "volta": {
        "url": "https://volta-rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "exosama": {
        "url": "https://rpc.exosama.com",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "joc-mainnet": {
        "url": "https://rpc-1.japanopenchain.org:8545",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "joc-testnet": {
        "url": "https://rpc-1.testnet.japanopenchain.org:8545",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "base-mainnet": {
        "url": "https://mainnet.base.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": True
    },
    "base-sepolia": {
        "url": "https://sepolia.base.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
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
    "linea-mainnet": {
        "url": "https://rpc.linea.build",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    },
    "linea-sepolia": {
        "url": "https://rpc.sepolia.linea.build",
        "blockReduced": 8192,
        "multiplierRequirement": 10000,
        "isPoS": False
    }
}

def fastBlocksSettings(configuration, apiUrl, blockReduced, multiplierRequirement, isPoS):
    if "etherscan" in apiUrl:
        latestBlock = int(json.loads(subprocess.getoutput(
            f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_blockNumber&apikey={key}"'))['result'], 16)
    else:
        data_req = '{"id":0,"jsonrpc":"2.0","method": "eth_blockNumber","params": []}'
        response = requests.post(apiUrl, headers=headers, data=data_req).text
        latestBlock = int(json.loads(response)['result'], 16)

    baseBlock = latestBlock - blockReduced
    baseBlock = baseBlock - baseBlock % multiplierRequirement

    if "etherscan" in apiUrl:
        pivot = json.loads(subprocess.getoutput(
            f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_getBlockByNumber&tag={hex(baseBlock)}&boolean=true&apikey={key}"'))
    else:
        data_req = '{"id":0,"jsonrpc":"2.0","method": "eth_getBlockByNumber","params": ["' + str(hex(baseBlock)) + '", false]}'
        pivot = json.loads(requests.post(apiUrl, headers=headers, data=data_req).text)

    pivotHash = pivot['result']['hash']
    pivotTotalDifficulty = int(pivot['result'].get('totalDifficulty', '0x0'), 16)

    print(configuration + ' LatestBlock: ' + str(latestBlock))
    print(configuration + ' PivotNumber: ' + str(baseBlock))
    print(configuration + ' PivotHash: ' + str(pivotHash))
    if not isPoS:
      print(configuration + ' PivotTotalDifficulty: ' + str(pivotTotalDifficulty))

    with open(f'{configsPath}/{configuration}.json', 'r') as mainnetCfg:
        data = json.load(mainnetCfg)

    data['Sync']['PivotNumber'] = baseBlock
    data['Sync']['PivotHash'] = pivotHash
  
    if not isPoS:
        data['Sync']['PivotTotalDifficulty'] = str(pivotTotalDifficulty)

    with open(f'{configsPath}/{configuration}.json', 'w') as mainnetCfgChanged:
        json.dump(data, mainnetCfgChanged, indent=2)

for config, value in configs.items():
    print(emoji.emojize(f"{config.capitalize()} section                                     :white_check_mark: "))
    fastBlocksSettings(config, value['url'], value['blockReduced'], value['multiplierRequirement'], value['isPoS'])
