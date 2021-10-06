# Nethermind-Teku theMerge

## How To Run

This testnet requires 2 terminal processes, one for Nethermind, one for a Teku node. See the per-terminal commands below.

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
dotnet run -c Release --no-build -- --config themerge_devnet_m3 --Merge.TerminalTotalDifficulty 50
```

## Terminal 2: Teku

build Teku
```
git clone https://github.com/ConsenSys/teku.git -b merge-interop
cd teku && ./gradlew distTar installDist
```

clean previous profiles
```
# on linux/mac
rm -rf ~/teku
```

generate Teku consensus genesis state
```
./build/install/teku/bin/teku genesis mock \
--output-file ./local-genesis.ssz \
--network=minimal \
--Xnetwork-altair-fork-epoch=0 \
--Xnetwork-merge-fork-epoch=1 \
--validator-count=256```

run Teku
```
./build/install/teku/bin/teku \
--eth1-endpoints=http://localhost:8550 \
--ee-fee-recipient-address=0xfe3b557e8fb62b89f4916b721be55ceb828dbd73 \
--Xinterop-enabled=true \
--Xinterop-number-of-validators=256 \
--Xinterop-owned-validator-start-index=0 \
--Xinterop-owned-validator-count=256 \
--network=minimal \
--Xnetwork-altair-fork-epoch=0 \
--Xnetwork-merge-fork-epoch=1 \
--Xnetwork-merge-total-terminal-difficulty 50 \
--p2p-enabled=false \
--initial-state ./local-genesis.ssz \
-l DEBUG
```
