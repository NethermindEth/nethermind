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
| Github Actions | [![[RUN] Consensus Legacy Tests](https://github.com/NethermindEth/nethermind/actions/workflows/run-consesus-legacy-tests.yml/badge.svg)](https://github.com/NethermindEth/nethermind/actions/workflows/run-consesus-legacy-tests.yml) [![[RUN] Nethermind/Ethereum Tests with Code Coverage](https://github.com/NethermindEth/nethermind/actions/workflows/run-nethermind-tests-with-code-coverage.yml/badge.svg)](https://github.com/NethermindEth/nethermind/actions/workflows/run-nethermind-tests-with-code-coverage.yml) [![[UPDATE] GitBook Docs](https://github.com/NethermindEth/nethermind/actions/workflows/update-gitbook-docs.yml/badge.svg)](https://github.com/NethermindEth/nethermind/actions/workflows/update-gitbook-docs.yml) | https://github.com/NethermindEth/nethermind/actions |

## Download and run:

[Windows](http://downloads.nethermind.io)<br/>
[Linux x64/arm64](http://downloads.nethermind.io)<br/>
[MacOS](http://downloads.nethermind.io)<br/>

It syncs fully on: 
* `Mainnet`
* `Goerli`
* `Rinkeby`
* `Ropsten`
* `xDai`
* `Poacore`
* `Sokol`
* `Energyweb`
* `Volta`
* `Kovan` (only fast sync and may fail if pWASM transactions appear)

**PPA**
(Tested on Ubuntu Series: `Focal`, `Bionic`, `Xenial` and `Trusty`)
1. `sudo add-apt-repository ppa:nethermindeth/nethermind`
1. `sudo apt install nethermind`
1. To execute the launcher
``nethermind``
1. To execute the runner
``nethermind --config mainnet``

**Homebrew**
1. `brew tap nethermindeth/nethermind`
1. `brew install nethermind`
1. To execute the launcher
``nethermind-launcher``
1. To execute the runner
``nethermind --config mainnet``

# Build from Source

## Prerequisites :construction:

**.NET 5.0** SDK

### Windows

* [Install .NET](https://www.microsoft.com/net/download)
* You may need to install [this](https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads).

### Linux

#### Ubuntu
* [Install .NET](https://docs.microsoft.com/en-gb/dotnet/core/install/linux-ubuntu)
* Install dependencies
```sh
sudo apt-get install libsnappy-dev libc6-dev libc6
```
*Tested on Ubuntu 21.04, 20.04 and 18.04 LTS*

#### Debian
* [Install .NET](https://docs.microsoft.com/en-gb/dotnet/core/install/linux-debian)
* Install dependencies
```sh
sudo apt-get install libsnappy-dev libc6-dev libc6
```
*Tested on Debian 10 (9 not working)*

#### CentOS
* [Install .NET](https://docs.microsoft.com/en-gb/dotnet/core/install/linux-centos)
* Install dependencies
```sh
sudo yum install -y glibc-devel bzip2-devel libzstd

# Link libraries
sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
```
*Tested on CentOS 8*

#### Fedora
* [Install .NET](https://docs.microsoft.com/en-gb/dotnet/core/install/linux-fedora)
* Install dependencies
```sh
sudo yum install -y glibc-devel snappy libzstd

# Link libraries
sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
```
*Tested on Fedora 32*

### MacOS

* [Install .NET](https://www.microsoft.com/net/download)
* Install dependencies
```sh
brew install rocksdb gmp snappy lz4 zstd
```

## Build and Run

```sh
git clone https://github.com/NethermindEth/nethermind --recursive
cd nethermind/src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
dotnet run -c Release --no-build -- --config mainnet
```

## Docker Image

Official Nethermind docker images are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

### Get digest of docker image

In case of any docker image need to be updated in the repository, you can update the digest of this images with the next commands

```sh
docker inspect --format='{{index .RepoDigests 0}}' <image_name>
```

The output must show the image digest, and then you can copy that output in the `FROM` tag inside the Dockerfile

## Test

If you want to run the Nethermind or Ethereum Foundation tests, then:
```sh
dotnet build Nethermind.sln -c Debug
dotnet test Nethermind.sln

dotnet build EthereumTests.sln -c Debug
dotnet test EthereumTests.sln
```

## IDE

* [JetBrains Rider](https://www.jetbrains.com/rider)
* [Visual Studio Code](https://code.visualstudio.com/docs/other/dotnet)


## Contributors welcome
[![GitHub Issues](https://img.shields.io/github/issues/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/issues)
[![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg)](https://gitter.im/nethermindeth/nethermind)
[![GitHub Contributors](https://img.shields.io/github/contributors/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/graphs/contributors)

At Nethermind we are building an open source multiplatform Ethereum client implementation in .NET Core (running seamlessly on Linux, Windows and MacOS). Simultaneously our team works on Nethermind Data Marketplace and on-chain data extraction tools and client customizations.

Nethermind client can be used in your projects, when setting up private Ethereum networks or dApps. The latest prod version of Nethermind can be found at downloads.nethermind.io.

# License
[![GitHub](https://img.shields.io/github/license/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/blob/master/LICENSE)

