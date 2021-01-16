<img src="Nethermind.png" width="200">

# .NET Core Ethereum client
|           |         |               |
| :-------- | :------ | :------------ |
| Documentation | | https://docs.nethermind.io |
| Gitter | [![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind) | https://gitter.im/nethermindeth/nethermind |
| Discord | [![Discord](https://img.shields.io/discord/629004402170134531)](https://discord.gg/GXJFaYk) |
| Medium | | https://medium.com/nethermind-eth |
| Twitter | | https://twitter.com/nethermindeth |
| Releases | [![GitHub release](https://img.shields.io/github/release/NethermindEth/nethermind.svg)](https://github.com/NethermindEth/nethermind/releases) | https://github.com/NethermindEth/nethermind/releases |
| Website | | https://nethermind.io/ |
|Docker||https://hub.docker.com/r/nethermind/nethermind|
|Codecov.io| [![codecov](https://codecov.io/gh/NethermindEth/nethermind/branch/master/graph/badge.svg)](https://codecov.io/gh/NethermindEth/nethermind) | https://codecov.io/gh/NethermindEth/nethermind |
| Fund | with Gitcoin | https://gitcoin.co/grants/142/nethermind |
| Github Actions | ![Standard Build](https://github.com/NethermindEth/nethermind/workflows/Standard%20Build/badge.svg) ![Build with Code Coverage](https://github.com/NethermindEth/nethermind/workflows/Build%20with%20Code%20Coverage/badge.svg) ![Update Documentation](https://github.com/NethermindEth/nethermind/workflows/Update%20Documentation/badge.svg) ![Publish Nethermind Image to Docker Registry](https://github.com/NethermindEth/nethermind/workflows/Publish%20Nethermind%20Image%20to%20Docker%20Registry/badge.svg) ![Publish ARM64 Image to Docker Registry](https://github.com/NethermindEth/nethermind/workflows/Publish%20ARM64%20Image%20to%20Docker%20Registry/badge.svg) | https://github.com/NethermindEth/nethermind/actions |

## Download and run:

[Windows](http://downloads.nethermind.io)<br/>
[Linux](http://downloads.nethermind.io)<br/>
[MacOS](http://downloads.nethermind.io)<br/>

It syncs fully on: 
* Mainnet
* Goerli
* Rinkeby
* Ropsten
* xDai
* Poacore
* Sokol
* Energyweb
* Volta
* Kovan (only fast sync and may fail if pWASM transactions appear)

# Build from Source

## Prerequisites :construction:

**.NET 3.1** SDK for 1.9.* series or **.NET 5.0** for 1.10.*

### Windows

* Install .NET https://www.microsoft.com/net/download
* You may need to install https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads

### Linux

#### - Ubuntu
```sh
# Install .NET
https://docs.microsoft.com/en-gb/dotnet/core/install/linux-ubuntu

# Install dependencies
sudo apt-get install libsnappy-dev libc6-dev libc6
```
*Tested on Ubuntu 20.04 LTS and 18.04 LTS*

#### - Debian
```sh

# Install .NET
https://docs.microsoft.com/en-gb/dotnet/core/install/linux-debian

# Install dependencies
sudo apt-get install libsnappy-dev libc6-dev libc6
```

*Tested on Debian 10 (9 not working)*


#### - CentOS
```sh
# Install .NET
https://docs.microsoft.com/en-gb/dotnet/core/install/linux-centos

# Install dependencies
sudo yum install -y glibc-devel bzip2-devel libzstd

# Link libraries
sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
```
*Tested on CentOS 8*

#### - Fedora
```sh
# Install .NET
https://docs.microsoft.com/en-gb/dotnet/core/install/linux-fedora

# Install dependencies
sudo yum install -y glibc-devel snappy libzstd

# Link libraries
sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
```
*Tested on Fedora 32*

### Mac

* Install .NET https://www.microsoft.com/net/download
* Install deps `brew install gmp snappy lz4 zstd`
* Additionally, if you have problems with startup `brew install rocksdb`

## Build and Run

```
git clone https://github.com/NethermindEth/nethermind --recursive
cd nethermind/src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
dotnet run -c Release --no-build -- --config mainnet
```

## Docker Image

Official Nethermind docker images are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

## Test

If you want to run the Nethermind or Ethereum Foundation tests, then:
```
dotnet build Nethermind.sln -c Debug
dotnet test Nethermind.sln

dotnet build EthereumTests.sln -c Debug
dotnet test EthereumTests.sln
```

## IDE

* JetBrains Rider ([Link](https://www.jetbrains.com/rider))
* Visual Studio Code ([Link](https://code.visualstudio.com/docs/other/dotnet))


## Contributors welcome
[![GitHub Issues](https://img.shields.io/github/issues/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/issues)
[![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind)
[![GitHub Contributors](https://img.shields.io/github/contributors/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/graphs/contributors)

At Nethermind we are building an open source multiplatform Ethereum client implementation in .NET Core (running seamlessly on Linux, Windows and MacOS). Simultaneously our team works on Nethermind Data Marketplace and on-chain data extraction tools and client customizations.

Nethermind client can be used in your projects, when setting up private Ethereum networks or dApps. The latest prod version of Nethermind can be found at downloads.nethermind.io.
# Links
https://nethermind.io/

# License
[![GitHub](https://img.shields.io/github/license/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/blob/master/LICENSE)

