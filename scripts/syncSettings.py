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


# Mainnet 
print(emoji.emojize("Mainnet section                                     :white_check_mark: ", use_aliases=True))
mainnetApiUrl = 'api.etherscan.io'
mainnetBlockReduced = 8192

fastBlocksSettings('mainnet', mainnetApiUrl, mainnetBlockReduced, 10000)

# Goerli
print(emoji.emojize("Goerli section                                      :white_check_mark: ", use_aliases=True))
goerliApiUrl = 'api-goerli.etherscan.io'
goerliBlockReduced = 8192

fastBlocksSettings('goerli', goerliApiUrl, goerliBlockReduced, 30000)

# Ropsten
print(emoji.emojize("Ropsten section                                     :white_check_mark: ", use_aliases=True))
ropstenApiUrl = 'api-ropsten.etherscan.io'
ropstenBlockReduced = 8192

fastBlocksSettings('ropsten', ropstenApiUrl, ropstenBlockReduced, 10000)

# Rinkeby
print(emoji.emojize("Rinkeby section                                     :white_check_mark: ", use_aliases=True))
rinkebyApiUrl = 'api-rinkeby.etherscan.io'
rinkebyBlockReduced = 8192

fastBlocksSettings('rinkeby', rinkebyApiUrl, rinkebyBlockReduced, 30000)

# POA Core
print(emoji.emojize("POA Core section                                     :white_check_mark: ", use_aliases=True))
poacoreApiUrl = 'https://core.poa.network'
poacoreBlockReduced = 8192

fastBlocksSettings('poacore', poacoreApiUrl, poacoreBlockReduced, 30000)

# xDai
print(emoji.emojize("xDai section                                     :white_check_mark: ", use_aliases=True))
xdaiApiUrl = 'https://dai.poa.network'
xdaiBlockReduced = 8192

fastBlocksSettings('xdai', xdaiApiUrl, xdaiBlockReduced, 30000)

# Sokol
print(emoji.emojize("Sokol section                                     :white_check_mark: ", use_aliases=True))
sokolApiUrl = 'https://sokol.poa.network'
sokolBlockReduced = 8192

fastBlocksSettings('sokol', sokolApiUrl, sokolBlockReduced, 30000)

print(emoji.emojize("Fast Sync configuration settings finished           :ok_hand: ", use_aliases=True))
