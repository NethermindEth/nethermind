# Nethermind-Lodestar theMerge

## How To Run

This testnet requires 2 terminal processes, one for Nethermind, one for a Lodestar node. See the per-terminal commands below.

## Terminal 1: Nethermind

### Install dotnet:
```
https://dotnet.microsoft.com/download
```

### Build Nethermind:
```
git clone --depth 1 https://github.com/NethermindEth/nethermind.git --recursive -b themerge
cd src/Nethermind
dotnet build Nethermind.sln -c Release
# if src/Nethermind/Nethermind.Runner/bin/Release/net5.0/plugins has no Nethermind.Merge.Plugin.dll plugin then you may need to run the build again
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
```

run Nethermind
```
dotnet run -c Release --no-build -- --config themerge_devnet
```

## Terminal 2: Lodestar

build Lodestar
```
git clone --depth 1 http://github.com/chainsafe/lodestar && cd lodestar
# make sure you have node version >= 14.0.0 before the next step
yarn && yarn build
```

run Lodestar
```
./lodestar dev --genesisValidators 32 --startValidators 0:31 \
  --api.rest.enabled --api.rest.host 0.0.0.0 \
  --logFile beacon.log --logLevelFile debug --logRotate --logMaxFiles 5 \
  --params.ALTAIR_FORK_EPOCH 0 \
  --params.MERGE_FORK_EPOCH 0 \
  --params.TRANSITION_TOTAL_DIFFICULTY 0 \
  --genesisEth1Hash "0x3b8fb240d288781d4aac94d3fd16809ee413bc99294a085798a589dae51ddd4a" \
  --execution.urls http://localhost:8550
```
