<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://user-images.githubusercontent.com/337518/184757509-5ac8a259-659a-42dd-a51c-cd093a41a0ad.png">
  <source media="(prefers-color-scheme: light)" srcset="https://user-images.githubusercontent.com/337518/184757473-5d70ac41-4afd-42f6-ab7b-5338ae09b2fb.png">
  <img alt="Nethermind" src="https://user-images.githubusercontent.com/337518/184757473-5d70ac41-4afd-42f6-ab7b-5338ae09b2fb.png" height="64">
</picture>

# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)](https://discord.gg/GXJFaYk)
[![Follow us on Twitter](https://img.shields.io/twitter/follow/nethermindeth?style=social&label=Follow)](https://twitter.com/nethermindeth)
[![Ask on Discourse](https://img.shields.io/discourse/posts?style=social&label=Community&logo=discourse&server=https%3A%2F%2Fcommunity.nethermind.io)](https://community.nethermind.io/c/nethermind-client)

Nethermind is a high-performance, highly configurable full Ethereum protocol execution client built on .NET that runs on Linux, Windows, and macOS, and supports Clique, Aura, and Ethash. Nethermind offers very fast sync speeds and support for external plugins. Enjoy reliable access to rich on-chain data thanks to high-performance JSON-RPC based on the Kestrel web server. Healthy node monitoring is secured with Grafana analytics and Seq logging.

## Documentation

Nethermind documentation is available at [docs.nethermind.io](https://docs.nethermind.io).

### Supported networks

**`Mainnet`** **`Goerli`** **`Sepolia`** **`Gnosis (xDai)`** **`Energy Web`** **`Volta`**

## Download and run

Release builds are available on the [Releases page](https://github.com/nethermindeth/nethermind/releases) and at [downloads.nethermind.io](https://downloads.nethermind.io).

#### On Linux using PPA

1. `sudo add-apt-repository ppa:nethermindeth/nethermind` \
   If command not found: `sudo apt install software-properties-common`
2. `sudo apt install nethermind`
3. To run the launcher: `nethermind`
4. To run the runner: `nethermind -c mainnet`

#### On Windows using Windows Package Manager

1. `winget install nethermind`
2. To run the launcher: `nethermind.launcher.exe`
3. To run the runner: `nethermind.runner.exe -c mainnet`

#### On macOS using Homebrew

1. `brew tap nethermindeth/nethermind`
2. `brew install nethermind`
3. To run the launcher: `nethermind-launcher`
4. To run the runner: `nethermind -c mainnet`

## Docker image

The official Docker images of Nethermind are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

### Get the digest of the Docker image

In case of any Docker image need to be updated in the repository, you can update the digest of these images as follows:

```sh
docker inspect --format='{{index .RepoDigests 0}}' <image_name>
```

The output should show the image digest, and then you can copy that to the `FROM` tag in the Dockerfile.

## Build from source

### Prerequisites

#### Windows

-   [Install .NET](https://dotnet.microsoft.com/en-us/download?initial-os=windows)

#### macOS

-   [Install .NET](https://dotnet.microsoft.com/en-us/download?initial-os=macos)
-   Install dependencies:

    ```sh
    brew install gmp snappy lz4 zstd
    ```

#### Ubuntu

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-ubuntu)
-   Install dependencies:

    ```sh
    sudo apt-get install libsnappy-dev libc6-dev libc6
    ```

    An extra dependency for aarch64 (arm64):

    ```sh
    sudo apt-get install libgflags-dev
    ```

#### Debian

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-debian)
-   Install dependencies:

    ```sh
    sudo apt-get install libsnappy-dev libc6-dev libc6
    ```

#### CentOS

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-centos)
-   Install dependencies:

    ```sh
    sudo yum install -y glibc-devel bzip2-devel libzstd

    # Link libraries
    sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0
    ```

#### Fedora

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-fedora)
-   Install dependencies:

    ```sh
    sudo yum install -y glibc-devel snappy libzstd

    # Link libraries
    sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0
    ```

### Build and run

```sh
git clone https://github.com/nethermindeth/nethermind --recursive
cd nethermind/src/Nethermind/Nethermind.Runner
dotnet run -c release -- -c mainnet
```

## Test

Run the Nethermind and/or Ethereum Foundation tests as follows:

```sh
dotnet test Nethermind.sln -c debug
dotnet test EthereumTests.sln -c debug
```

## Contributing

BEFORE you start work on a feature or fix, please read and follow our [contribution guide](https://github.com/nethermindeth/nethermind/blob/master/CONTRIBUTING.md) to help avoid any wasted or duplicate effort.

## License

Nethermind is an open-source software licensed under the [LGPL-3.0](https://github.com/nethermindeth/nethermind/blob/master/LICENSE-LGPL).
