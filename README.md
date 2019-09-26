# Cortex

.NET Core Ethereum 2.0

## Getting started

### Pre-requisites

* .NET Core 3.0 development tools
* PowerShell Core, to run build scripts
* An editor, e.g. VS Code, if you want to contribute

### Compile and run the Beacon Node

```
dotnet run --project src/Cortex.BeaconNode
```

### Test it works

Open a browser to ```http://localhost:5000/node/version``` and it should respond with the name and version.

### Build with version number

To build a release version, with a gitversion based version number:

```
./build.ps1
```

Then run the DLL that was created:

```
dotnet ./src/Cortex.BeaconNode/bin/Release/netcoreapp3.0/publish/Cortex.BeaconNode.dll
```


## To Do

* Code everything !!!

* Commands to install as a Windows Service

https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-3.0&tabs=netcore-cli



