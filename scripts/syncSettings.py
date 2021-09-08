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

print(emoji.emojize("Fast Sync configuration settings initialization     :white_check_mark: ", use_aliases=True))

configs = {
    # fast sync section
    "mainnet": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000
    },
    "goerli": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "ropsten": {
        "url": "api-ropsten.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "rinkeby": {
        "url": "api-rinkeby.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000
    },
    "poacore": {
        "url": "https://core.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "xdai": {
        "url": "https://dai.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "sokol": {
        "url": "https://sokol.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
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
    # mev section
    "mainnet_mev": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000
    },
    "goerli_mev": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "xdai_mev": {
        "url": "https://dai.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    # pruned mev section
    "mainnet_pruned_mev": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000
    },
    "goerli_pruned_mev": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "xdai_pruned_mev": {
        "url": "https://dai.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    # beam section
    "mainnet_beam": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000 
    },
    "goerli_beam": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "ropsten_beam": {
        "url": "api-ropsten.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "rinkeby_beam": {
        "url": "api-rinkeby.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000
    },
    "poacore_beam": {
        "url": "https://core.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    # pruning section
    "mainnet_pruned": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000 
    },
    "goerli_pruned": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "ropsten_pruned": {
        "url": "api-ropsten.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "rinkeby_pruned": {
        "url": "api-rinkeby.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000
    },
    "poacore_pruned": {
        "url": "https://core.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "xdai_pruned": {
        "url": "https://dai.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "sokol_pruned": {
        "url": "https://sokol.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "energyweb_pruned": {
        "url": "https://rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "volta_pruned": {
        "url": "https://volta-rpc.energyweb.org",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    # ndm section
    "ndm_consumer_mainnet": {
        "url": "api.etherscan.io",
        "blockReduced": 2000,
        "multiplierRequirement": 2000 
    },
    "ndm_consumer_xdai": {
        "url": "https://dai.poa.network",
        "blockReduced": 8192,
        "multiplierRequirement": 10000
    },
    "ndm_consumer_goerli": {
        "url": "api-goerli.etherscan.io",
        "blockReduced": 8192,
        "multiplierRequirement": 30000 
    },
    "ndm_consumer_ropsten": {
        "url": "api-ropsten.etherscan.io",
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
    print(emoji.emojize(f"{config.capitalize()} section                                     :white_check_mark: ", use_aliases=True))
    fastBlocksSettings(config, value['url'], value['blockReduced'], value['multiplierRequirement'])
