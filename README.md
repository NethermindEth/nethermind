<img src="Nethermind.png" width="200">

# .NET Core Ethereum client
|           |         |               |
| :-------- | :------ | :------------ |
| Documentation | [![ReadtheDocs](https://readthedocs.org/projects/nethermind/badge/?version=latest)](https://nethermind.readthedocs.io) | https://nethermind.readthedocs.io |
| Gitter | [![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind) | https://gitter.im/nethermindeth/nethermind |
| One day we will tweet at |  | https://twitter.com/nethermindeth |
| Releases | [![GitHub release](https://img.shields.io/github/release/NethermindEth/nethermind.svg)](https://github.com/NethermindEth/nethermind/releases) | https://github.com/NethermindEth/nethermind/releases |
| Travis CI | [![Build Status](https://travis-ci.org/NethermindEth/nethermind.svg?branch=master)](https://travis-ci.org/NethermindEth/nethermind) | https://travis-ci.org/NethermindEth/nethermind |
| Website | | http://nethermind.io/ |
|Docker||https://hub.docker.com/r/nethermind/nethermind|
|Codecov.io| [![codecov](https://codecov.io/gh/NethermindEth/nethermind/branch/master/graph/badge.svg)](https://codecov.io/gh/NethermindEth/nethermind) | https://codecov.io/gh/NethermindEth/nethermind |

## Download and run:

[Windows](http://downloads.nethermind.io)<br/>
[Linux](http://downloads.nethermind.io)<br/>
[MacOS](http://downloads.nethermind.io)<br/>

it syncs fully on Mainnet, Ropsten, Rinkeby, Goerli

## Build from Source

### Prerequisites

.NET 2.2 SDK

#### Windows

*	Windows https://www.microsoft.com/net/download?initial-os=windows
* you may need to install https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads

#### Linux

*	Linux https://www.microsoft.com/net/download?initial-os=linux (make sure to select the right distribution)
* `sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6`

#### Mac

*	Mac https://www.microsoft.com/net/download?initial-os=macos
* `brew install gmp && brew install snappy && brew install lz4`

### Build

```
git clone https://github.com/tkstanczak/nethermind --recursive
cd nethermind/src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
dotnet run -c Release --no-build -- --config mainnet
```

### Test

if you want to run the Nethermind or Ethereum Foundation tests, then:
```
dotnet build Nethermind.sln -c Debug
dotnet test Nethermind.sln

dotnet build EthereumTests.sln -c Debug
dotnet test EthereumTests.sln
```

### IDE

•	JetBrains Rider https://www.jetbrains.com/rider/<br/>
•	VS Code https://code.visualstudio.com/docs/other/dotnet<br/>


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

