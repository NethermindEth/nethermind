# Nethermind Ethereum 2.0 - Beacon Node Host

This is the Beacon Node host for Ethereum 2.0 for the .NET Core Nethermind project.

## Getting started

### Pre-requisites

* .NET Core 3.0 development tools
* On Linux and OSX, you also need GMP installed (big number library)

### Compile and run the Beacon Node

To run the unit tests:

```
dotnet test src/Nethermind/Nethermind.BeaconNode.Test
```

To run with default Development settings (minimal config), with mocked quick start:

```
dotnet run --project src/Nethermind/Nethermind.BeaconNode.Host --QuickStart:GenesisTime ([DateTimeOffset]::Now.ToUnixTimeSeconds()) --QuickStart:ValidatorCount 8
```

### Test it works

Run, as above, then open a browser to ```https://localhost:5001/node/version``` and it should respond with the name and version.

Other GET queries:

* genesis time: ```https://localhost:5001/node/genesis_time```
* get an unsigned block, ready for signing: ```https://localhost:5001/validator/block?slot=10&randao_reveal=0x0102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f10```

(this last one is only mocked up so far, and not actually implemented yet)

### Optional requirements

* PowerShell Core, to run build scripts
* An editor, e.g. VS Code, if you want to contribute

### Build with version number

To publish a release version, configured for Production environment:

```
./src/Nethermind/Nethermind.BeaconNode.Host/build.ps1
```

Then run the DLL that was published:

```
dotnet ./src/Nethermind/Nethermind.BeaconNode.Host/release/latest/Nethermind.BeaconNode.Host.dll
```

From the published version you can also start with Development (minimal) configuration, and quick start parameters:

```
dotnet ./src/Nethermind/Nethermind.BeaconNode.Host/release/latest/Nethermind.BeaconNode.Host.dll --Environment Development --QuickStart:GenesisTime 1578009600 --QuickStart:ValidatorCount 8
```

## Development

### Configuration files

Primary configuration files use .NET Core JSON settings files, with overrides from envrionment variables and command line.

There is a script in src/Nethermind/Nethermind.BeaconNode.Host/configuration that will convert from the specification YAML files to fragments to insert into the relevant Production and Development configuration JSON files.

For backwards compatibility, the application can also use the YAML files directly if necessary (although values are overwritten by anything from the full appsettings).

### API generation

Controller code, in the Nethermind.BeaconNode.Api project:

```
cd src/Nethermind/Nethermind.BeaconNode.Api
dotnet restore # ensure the tool is installed
dotnet nswag openapi2cscontroller /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeApi /namespace:Nethermind.BeaconNode.Api /output:BeaconNodeApi-generated.cs /UseLiquidTemplates:true /AspNetNamespace:"Microsoft.AspNetCore.Mvc" /ControllerBaseClass:"Microsoft.AspNetCore.Mvc.Controller"
```

Client code:

```
dotnet nswag openapi2csclient /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeClient /namespace:Nethermind.BeaconNode.ApiClient /ContractsNamespace:Nethermind.BeaconNode.ApiClient.Contracts /output:../Nethermind.BeaconNode.ApiClient/BeaconNodeClient-generated.cs
```

### Implemented

Impelmented so far:

Phase 0:

* The Beacon Chain
* Fork Choice -- main spec implemented, alternate algorithms are not

Supporting components:

* SimpleSerialize (SSZ) spec -- sufficient to support the beacon chain, no deserialisation yet, see the separate Cortex.Ssz project, https://github.com/NethermindEth/cortex-ssz
* BLS signature verification --  sufficient to support the beacon chain, currently only Windows support, based on the Herumi library, see the separate Cortex.Cryptography.Bls project, https://github.com/NethermindEth/cortex-cryptography-bls

Both these will be merged into the main Nethermind project / renamed / replaced with the Nethermind implementation.

### In Progress

* Honest Validator
* Eth2 APIs
* Interop Standards in Eth2 PM -- partially implemented for basic mocked QuickStart

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
* Installation, e.g.Windows Service (to be intergrated with Nethermind.Runner ?)

https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-3.0&tabs=netcore-cli


## License

Copyright (C) 2019 Demerzel Solutions Limited

This library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License and GNU General Public License for more details.

You should have received a copy of the GNU Lesser General Public License and GNU General Public License along with this library. If not, see <https://www.gnu.org/licenses/>.
