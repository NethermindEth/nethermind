# Cortex

.NET Core Ethereum 2.0

## Getting started

### Pre-requisites

* .NET Core 3.0 development tools

### Compile and run the Beacon Node

To run with default Development settings (minimal config):

```
dotnet run --project src/Cortex.BeaconNode.Host --QuickStart:GenesisTime 1578009600 --QuickStart:ValidatorCount 3
```

To run with Production settings (mainnet config):

```
dotnet run --project src/Cortex.BeaconNode.Host --environment Production
```


### Test it works

Open a browser to ```http://localhost:5000/node/version``` and it should respond with the name and version.

### Optional requirements

* PowerShell Core, to run build scripts
* An editor, e.g. VS Code, if you want to contribute

### Build with version number

To build a release version, with a gitversion based version number:

```
./build.ps1
```

Then run the DLL that was created (still the Development version):

```
dotnet ./src/Cortex.BeaconNode.Host/bin/Release/netcoreapp3.0/publish/Cortex.BeaconNode.Host.dll
```

The files are also copied to a release directory, configured with default host settings as Production:

```
dotnet ./release/latest/Cortex.BeaconNode.Host.dll
```

The published version also includes a published Windows platform executable.


## Development

### API generation

Controller code:

```
dotnet tools/nswag/dotnet-nswag.dll openapi2cscontroller /input:docs/beacon-node-oapi.yaml /classname:BeaconNodeApi /namespace:Cortex.BeaconNode.Api /output:src/Cortex.BeaconNode.Api/BeaconNodeApi-generated.cs /UseLiquidTemplates:true /AspNetNamespace:"Microsoft.AspNetCore.Mvc" /ControllerBaseClass:"Microsoft.AspNetCore.Mvc.Controller"
```

Client code:

```
dotnet tools/nswag/dotnet-nswag.dll openapi2csclient /input:docs/beacon-node-oapi.yaml /classname:BeaconNodeClient /namespace:Cortex.BeaconNode.ApiClient /ContractsNamespace:Cortex.BeaconNode.ApiClient.Contracts /output:src/Cortex.BeaconNode.ApiClient/BeaconNodeClient-generated.cs
```

### Implemented

Impelmented so far:

Phase 0:

* The Beacon Chain
* Fork Choice -- main spec implemented, alternate algorithms are not

Supporting components:

* SimpleSerialize (SSZ) spec -- sufficient to support the beacon chain, no deserialisation yet, see the separate Cortex.Ssz project, https://github.com/sgryphon/cortex-ssz
* BLS signature verification --  sufficient to support the beacon chain, currently only Windows support, based on the Herumi library, see the separate Cortex.Cryptography.Bls project, https://github.com/sgryphon/cortex-cryptography-bls

### In Progress

* Honest Validator
* Eth2 APIs
* Interop Standards in Eth2 PM -- QuickStart

### To Do

Phase 0:

* Deposit Contract

Phase 1:

* Custody Game
* Shard Data Chains
* Misc beacon chain updates

Supporting:

* General test format
* Merkle proof formats
* Light client syncing protocol

Other: 

* Eth2 Metrics

Project-specific:

* Peer to peer
* Installation, e.g.Windows Service

https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-3.0&tabs=netcore-cli


## License

Copyright (C) 2019 Demerzel Solutions Limited

This library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License and GNU General Public License for more details.

You should have received a copy of the GNU Lesser General Public License and GNU General Public License along with this library. If not, see <https://www.gnu.org/licenses/>.
