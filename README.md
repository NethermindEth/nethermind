<img src="Nethermind.png" width="200">

# .NET Core Ethereum client

[![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind)
[![Build Status](https://travis-ci.org/NethermindEth/nethermind.svg?branch=master)](https://travis-ci.org/NethermindEth/nethermind)
[![GitHub release](https://img.shields.io/github/release/NethermindEth/nethermind.svg)](https://github.com/NethermindEth/nethermind/releases)

## Download and run:
[Windows](http://downloads.nethermind.io)<br/>
[Linux](http://downloads.nethermind.io)<br/>
[MacOS](http://downloads.nethermind.io)<br/>

## Build (Windows / Linux / MacOS)

### IDE
•	JetBrains Rider https://www.jetbrains.com/rider/<br/>
•	VS Code https://code.visualstudio.com/docs/other/dotnet<br/>

### SDKs
•	Windows https://www.microsoft.com/net/download?initial-os=windows<br/>
•	Linux https://www.microsoft.com/net/download?initial-os=linux (make sure to select the right distribution)<br/>
•	Mac https://www.microsoft.com/net/download?initial-os=macos<br/>

### source and build

on Linux:
```
sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6
```

on Mac:
```
brew install gmp
```

then (any platform):
```
git clone https://github.com/tkstanczak/nethermind --recursive
cd nethermind/src/Nethermind
dotnet build -c Release
cd Nethermind.Runner
dotnet run
```

## Contributors welcome
[![GitHub issues](https://img.shields.io/github/issues/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/issues)
[![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind)
[![GitHub contributors](https://img.shields.io/github/contributors/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/graphs/contributors)

At Nethermind we are building an Open Source multiplatform Ethereum client implementation in .NET Core (running seamlessly on Linux, Windows and MacOS). Simultaneously our team works on Nethermind trading tools, analytics and decentralized exchange (0x relay).

Nethermind client can be used in your projects, when setting up private Ethereum networks or dApps. Nethermind is under development and you find the open issues here [issues](https://github.com/NethermindEth/nethermind/issues)

# Links
http://nethermind.io/

# License
[![GitHub](https://img.shields.io/github/license/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/blob/master/LICENSE)

