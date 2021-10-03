# Nethermind-Nimbus theMerge

## How To Run

This testnet requires 2 terminal processes, one for Nethermind, one for a Nimbus node. See the per-terminal commands below.

## Terminal 1: Nethermind

### Install dotnet:
```
https://dotnet.microsoft.com/download
```

### Build Nethermind:
```
git clone https://github.com/NethermindEth/nethermind.git --recursive -b themerge
cd src/Nethermind
dotnet build Nethermind.sln -c Release
# if src/Nethermind/Nethermind.Runner/bin/Release/net5.0/plugins has no Nethermind.Merge.Plugin.dll plugin then you may need to run the build again
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
```

run Nethermind
```
rm -rf bin/Release/net5.0/nethermind_db
dotnet run -c Release --no-build -- --config themerge_devnet
```

## Terminal 2: Nimbus
```
git clone https://github.com/status-im/nimbus-eth2.git -b amphora-merge-interop
make update -j$(nproc)
make nimbus_beacon_node -j$(nproc)
```

### Verify
```
./env.sh nim c -r tests/test_merge_vectors.nim
```
it may be needed to disable CPU instructions (e.g. when you run it inside Windows Subsystem for Linux)
```
./env.sh nim c -r -d:disableMarchNative tests/test_merge_vectors.nim
```
if you have a linking error mentioning libbacktrace then you can disable it as well:
```
./env.sh nim c -r -d:disableMarchNative -d:disable_libbacktrace tests/test_merge_vectors.nim
```

replace the JSON RPC port for test vectors 
```
  vi tests/test_merge_vectors.nim
  # then change the line below
  let web3Provider = (waitFor newWeb3DataProvider(
    default(Eth1Address), "ws://127.0.0.1:8550")).get
```

replace the JSON RPC port for local testnets 
```
vi scripts/launch_local_tetsnet.sh
# then change the line below
WEB3_ARG="--web3-url=ws://127.0.0.1:8551"
```

### Run

```
export NIMFLAGS="-d:disableMarchNative -d:disable_libbacktrace"
./scripts/launch_local_testnet.sh --preset minimal --nodes 4 --disable-htop --stop-at-epoch 7 -- --verify-finalization --discv5:no
```
