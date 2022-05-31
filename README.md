<img src="Nethermind.png" width="200">

# .NET Core Ethereum client

Nethermind is a is a high-performance, highly configurable full Ethereum protocol client built on .NET Core that runs on Linux, Windows and MacOS, and supports Clique, AuRa, Ethash and Proof of Stake consensus algorithms. Nethermind offers very fast sync speeds and support for external plug-ins. Enjoy reliable access to rich on-chain data thanks to high performance JSON-RPC based on Kestrel web server. Healthy node monitoring is secured with a Grafana dashboard and Seq enterprise logging.

[![Documentation](https://img.shields.io/badge/GitBook-docs-7B36ED?style=for-the-badge&logo=gitbook&logoColor=white)](https://docs.nethermind.io)
[![Releases](https://img.shields.io/github/release/NethermindEth/nethermind.svg?style=for-the-badge&logo=github&logoColor=white)](https://github.com/NethermindEth/nethermind/releases)
[![Docker Pulls](https://img.shields.io/docker/pulls/nethermind/nethermind?style=for-the-badge&logo=docker&logoColor=white)](https://hub.docker.com/r/nethermind/nethermind)
[![Codecov](https://img.shields.io/codecov/c/github/nethermindeth/nethermind?style=for-the-badge&logo=codecov&logoColor=white)](https://codecov.io/gh/NethermindEth/nethermind)
[![Website](https://img.shields.io/website?down_color=lightgrey&down_message=offline&style=for-the-badge&up_color=brightgreen&up_message=online&url=https%3A%2F%2Fnethermind.io)](https://nethermind.io)

### :speaking_head:	Chats
[![Discord](https://img.shields.io/discord/629004402170134531?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/GXJFaYk)
[![Gitter](https://img.shields.io/gitter/room/nethermindeth/nethermind.svg?style=for-the-badge&logo=gitter&logoColor=white)](https://gitter.im/nethermindeth/nethermind)

### :loudspeaker:	Social
[![Twitter Follow](https://img.shields.io/twitter/follow/nethermindeth?style=for-the-badge&logo=twitter&logoColor=white)](https://twitter.com/nethermindeth)
[![LinkedIn Follow](https://img.shields.io/badge/LinkedIn-follow-0077B5?style=for-the-badge&logo=linkedin&logoColor=white)](https://www.linkedin.com/company/nethermind)
[![Medium Follow](https://img.shields.io/badge/Medium-articles-12100E?style=for-the-badge&logo=medium&logoColor=white)](https://medium.com/nethermind-eth)

## Download and run

[![Windows](https://img.shields.io/badge/Windows-AMD64-0078D6?style=for-the-badge&logo=windows&logoColor=white)](http://downloads.nethermind.io)
[![Linux](https://img.shields.io/badge/Linux-AMD64/ARM64-FCC624?style=for-the-badge&logo=linux&logoColor=black)](http://downloads.nethermind.io)
[![MacOS](https://img.shields.io/badge/MacOS-AMD64/ARM64-000000?style=for-the-badge&logo=apple&logoColor=white)](http://downloads.nethermind.io)

### :chains: Currently supported list of networks

| `Network  name`  | 
| :------------    |
| Mainnet          |
| Goerli           |
| Rinkeby          |
| Ropsten          |
| Sepolia          |
| xDai (Gnosis)    |
| Poacore          |
| Sokol            |
| EnergyWeb        |
| Volta            |
| Kovan            |

#### Using PPA
(Tested on Ubuntu Series: `Focal`, `Bionic`, `Xenial` and `Trusty`)
1. `sudo add-apt-repository ppa:nethermindeth/nethermind`
1. `sudo apt install nethermind`
1. To execute the launcher
``nethermind``
1. To execute the runner
``nethermind --config mainnet_pruned``

#### Using Homebrew
1. `brew tap nethermindeth/nethermind`
1. `brew install nethermind`
1. To execute the launcher
``nethermind-launcher``
1. To execute the runner
``nethermind --config mainnet_pruned``

# Build from Source

## :construction: Prerequisites 

[![.NET SDK](https://img.shields.io/badge/SDK-6.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
)](https://dotnet.microsoft.com/en-us/download)

### Windows

* [Install .NET](https://www.microsoft.com/net/download)
* You may need to install [this](https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads).

### Linux

#### Ubuntu
* [Install .NET](https://docs.microsoft.com/en-gb/dotnet/core/install/linux-ubuntu)
* Install dependencies
```sh
sudo apt-get install libsnappy-dev libc6-dev libc6

# Link libraries (only for Ubuntu >= 21.04)
sudo ln -s /usr/lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so
```
*Tested on Ubuntu 21.04, 20.04 and 18.04 LTS and 21.10*

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
# also required for Fedora 35
sudo ln -s `find /usr/lib64/ -type f -name "libdl.so.2*"` /usr/lib64/libdl.so
```
*Tested on Fedora 32*

### MacOS

* [Install .NET](https://www.microsoft.com/net/download)
* Install dependencies
```sh
brew install rocksdb gmp snappy lz4 zstd
```

* Apple Silicon (M1) users only: create symlink for homebrew dependencies
```
sudo ln -s `find /opt/homebrew/Cellar/snappy -name "libsnappy.dylib"` /usr/local/lib/libsnappy.dylib
```

## :building_construction: Build and Run

```sh
git clone https://github.com/NethermindEth/nethermind --recursive
cd nethermind/src/Nethermind
dotnet build Nethermind.sln -c Release
cd Nethermind.Runner
dotnet run -c Release --no-build --config mainnet
```

## :whale: Docker Image

Official Nethermind docker images are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

### Get digest of docker image

In case of any docker image need to be updated in the repository, you can update the digest of this images with the next commands

```sh
docker inspect --format='{{index .RepoDigests 0}}' <image_name>
```

The output must show the image digest, and then you can copy that output in the `FROM` tag inside the Dockerfile

## :test_tube: Test

If you want to run the Nethermind or Ethereum Foundation tests, then:
```sh
dotnet build Nethermind.sln -c Debug
dotnet test Nethermind.sln

dotnet build EthereumTests.sln -c Debug
dotnet test EthereumTests.sln
```

## :bricks:	IDE

[![JetBrains Rider](https://img.shields.io/badge/Rider-000000?style=for-the-badge&logo=Rider&logoColor=white)](https://www.jetbrains.com/rider)
[![Visual Studio Code](https://img.shields.io/badge/Visual_Studio_Code-0078D4?style=for-the-badge&logo=visual%20studio%20code&logoColor=white)](https://code.visualstudio.com/docs/other/dotnet)
[![Visual Studio](https://img.shields.io/badge/Visual_Studio-5C2D91?style=for-the-badge&logo=visual%20studio&logoColor=white)](https://visualstudio.microsoft.com/downloads)

## :footprints:	Contributors welcome
[![GitHub Issues](https://img.shields.io/github/issues/nethermindeth/nethermind.svg?style=for-the-badge&logo=github&logoColor=white)](https://github.com/NethermindEth/nethermind/issues)
[![GitHub Contributors](https://img.shields.io/github/contributors/nethermindeth/nethermind.svg?style=for-the-badge&logo=github&logoColor=white)](https://github.com/NethermindEth/nethermind/graphs/contributors)

## License
[![GitHub](https://img.shields.io/github/license/nethermindeth/nethermind.svg)](https://github.com/NethermindEth/nethermind/blob/master/LICENSE)
