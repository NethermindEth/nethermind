### Setup Nimbus
```
git clone https://github.com/status-im/nimbus-eth2.git -b amphora-testnet-merge
make update -j$(nproc)
make nimbus_beacon_nodgit adde -j$(nproc)
```

### Setup Nethermind

Install dotnet:
```
https://dotnet.microsoft.com/download
```

Build Nethermind:
```
git clone https://github.com/NethermindEth/nethermind.git --recursive -b themerge
cd src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
# if src/Nethermind/Nethermind.Runner/bin/Release/net5.0/plugins has no Nethermind.Merge.Plugin.dll plugin then you may need to run the build again
dotnet build Nethermind.sln -c Release
```

### Verify
```
./env.sh nim c -r tests/test_merge_vectors.nim
```