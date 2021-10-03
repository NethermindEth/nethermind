### Setup Nimbus
```
git clone https://github.com/status-im/nimbus-eth2.git -b amphora-testnet-merge
make update -j$(nproc)
make nimbus_beacon_node -j$(nproc)
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
dotnet build Nethermind.sln
```

### Verify
```
./env.sh nim c -r tests/test_merge_vectors.nim
```