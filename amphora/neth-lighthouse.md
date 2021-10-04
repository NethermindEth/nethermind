## Nethermind-Lighthouse the Merge

## How To Run

This testnet requires 3 terminal processes, one for Nethermind one for a beacon node
and one for a validator client. See the per-terminal commands below.

### Terminal 1: Nethermind

Install dotnet:
```
https://dotnet.microsoft.com/download
```
*Arch Linux users will need `pacman -S dotnet-sdk aspnet-runtime`.*

Build Nethermind:
```
git clone https://github.com/NethermindEth/nethermind.git --recursive -b themerge
cd src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
# if src/Nethermind/Nethermind.Runner/bin/Release/net5.0/plugins has no Nethermind.Merge.Plugin.dll plugin then you may need to run the build again
dotnet build Nethermind.sln -c Release
dotnet run -c Release --no-build -- --config themerge_devnet
```

### Terminal 2: Lighthouse Beacon Node

install lighthouse
```
git clone https://github.com/sigp/lighthouse.git -b merge-f2f
cd lighthouse
make
make install-lcli
cd ..
```

```bash
git clone https://github.com/sigp/lighthouse-merge-f2f.git
cd lighthouse-merge-f2f
# make sure jq is installed before the next step
./m2_lighthouse/start_beacon_node.sh 8550
```

*Note: it's important to start the beacon node before the validator client
since that script also generates the testnet configuration.*

### Terminal 3: Lighthouse Validator Client

```bash
./m2_lighthouse/start_validator_client.sh
```
