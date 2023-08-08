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
        "multiplierRequirement": 1000
    },
    "goerli": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000
    },
    "gnosis": {
        "url": "https://rpc.gnosischain.com",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "chiado": {
        "url": "https://rpc.chiadochain.net",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "sepolia": {
        "url": "api-sepolia.etherscan.io",
        "blockReduced": 1000,
        "multiplierRequirement": 1000
    },
    "energyweb": {
        "url": "https://rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "volta": {
        "url": "https://volta-rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "exosama": {
        "url": "https://rpc.exosama.com",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    }
}

def fastBlocksSettings(configuration, apiUrl, blockReduced, multiplierRequirement):
    if "etherscan" in apiUrl:
        latestBlock = int(json.loads(subprocess.getoutput(f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_blockNumber&apikey={key}"'))['result'],16)
    else:
        data = '{"id":0,"jsonrpc":"2.0","method": "eth_blockNumber","params": []}'

        response = requests.post(apiUrl, headers=headers, data=data).text
        latestBlock = int(json.loads(response)['result'], 16)

    baseBlock = latestBlock - blockReduced
    baseBlock = baseBlock - baseBlock % multiplierRequirement

    if "etherscan" in apiUrl:
        pivot = json.loads(subprocess.getoutput(f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_getBlockByNumber&tag={hex(baseBlock)}&boolean=true&apikey={key}"'))
    else:
        data = '{"id":0,"jsonrpc":"2.0","method": "eth_getBlockByNumber","params": ["' +str(hex(baseBlock))+ '", false]}'
        pivot = json.loads(requests.post(apiUrl, headers=headers, data=data).text)

    pivotHash = pivot['result']['hash']
    pivotTotalDifficulty = int(pivot['result']['totalDifficulty'],16)
    print(configuration + 'LatestBlock: ' + str(latestBlock))
    print(configuration + 'PivotNumber: ' + str(baseBlock))
    print(configuration + 'PivotHash: ' + str(pivotHash))
    print(configuration + 'PivotTotalDifficulty: ' + str(pivotTotalDifficulty))
    data = {}
    with open(f'{configsPath}/{configuration}.cfg', 'r') as mainnetCfg:
        data = json.load(mainnetCfg)
        data['Sync']['PivotNumber'] = baseBlock
        data['Sync']['PivotHash'] = pivotHash
        data['Sync']['PivotTotalDifficulty'] = str(pivotTotalDifficulty)
        with open(f'{configsPath}/{configuration}.cfg', 'w') as mainnetCfgChanged:
            json.dump(data, mainnetCfgChanged, indent=2)

for config, value in configs.items():
    print(emoji.emojize(f"{config.capitalize()} section                                     :white_check_mark: "))
    fastBlocksSettings(config, value['url'], value['blockReduced'], value['multiplierRequirement'])
