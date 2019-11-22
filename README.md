# Cortex

.NET Core Ethereum 2.0

## Getting started

### Pre-requisites

* .NET Core 3.0 development tools

### Compile and run the Beacon Node

To run with the ```minimal.yaml``` config (default if not specified is ```mainnet.yaml```)

```
dotnet run --project src/Cortex.BeaconNode.Host --config minimal
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

Then run the DLL that was created:

```
dotnet ./src/Cortex.BeaconNode.Host/bin/Release/netcoreapp3.0/publish/Cortex.BeaconNode.Host.dll
```

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

## Contributions

Donations (ETH) can be sent to 0x1a474C09EE765C17fbf35B8B0fcb28a2B0E6e6db

Help me try and reach the 32 Eth needed to be a validator.

## To Do

* Code everything !!!

* Commands to install as a Windows Service

https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-3.0&tabs=netcore-cli

## License

Copyright (C) 2019 Gryphon Technology Pty Ltd

This library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License and GNU General Public License for more details.

You should have received a copy of the GNU Lesser General Public License and GNU General Public License along with this library. If not, see <https://www.gnu.org/licenses/>.
