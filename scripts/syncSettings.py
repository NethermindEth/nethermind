import json
import subprocess
import emoji
import sys

configsPath = './src/Nethermind/Nethermind.Runner/configs'

key = sys.argv[1]

print(emoji.emojize("Fast Sync configuration settings initialization     :white_check_mark: ", use_aliases=True))

def fastBlocksSettings(configuration, apiUrl, blockReduced):
    latestBlock = int(json.loads(subprocess.getoutput(f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_blockNumber&apikey={key}"'))['result'],16)
    baseBlock = round(latestBlock - blockReduced, -3)
    pivot = subprocess.getoutput(f'curl --silent "https://{apiUrl}/api?module=proxy&action=eth_getBlockByNumber&tag={hex(baseBlock)}&boolean=true&apikey={key}"')
    pivotHash = json.loads(pivot)['result']['hash']
    pivotTotalDifficulty = int(json.loads(pivot)['result']['totalDifficulty'],16)
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

fastBlocksSettings('mainnet', mainnetApiUrl, mainnetBlockReduced)

# Goerli
print(emoji.emojize("Goerli section                                      :white_check_mark: ", use_aliases=True))
goerliApiUrl = 'api-goerli.etherscan.io'
goerliBlockReduced = 30000

fastBlocksSettings('goerli', goerliApiUrl, goerliBlockReduced)

# Ropsten
print(emoji.emojize("Ropsten section                                     :white_check_mark: ", use_aliases=True))
ropstenApiUrl = 'api-ropsten.etherscan.io'
ropstenBlockReduced = 8192

fastBlocksSettings('ropsten', ropstenApiUrl, ropstenBlockReduced)

# Rinkeby
print(emoji.emojize("Rinkeby section                                     :white_check_mark: ", use_aliases=True))
rinkebyApiUrl = 'api-rinkeby.etherscan.io'
rinkebyBlockReduced = 30000

fastBlocksSettings('rinkeby', rinkebyApiUrl, rinkebyBlockReduced)

print(emoji.emojize("Fast Sync configuration settings finished           :ok_hand: ", use_aliases=True))
