<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://user-images.githubusercontent.com/337518/184757509-5ac8a259-659a-42dd-a51c-cd093a41a0ad.png">
  <source media="(prefers-color-scheme: light)" srcset="https://user-images.githubusercontent.com/337518/184757473-5d70ac41-4afd-42f6-ab7b-5338ae09b2fb.png">
  <img alt="Nethermind" src="https://user-images.githubusercontent.com/337518/184757473-5d70ac41-4afd-42f6-ab7b-5338ae09b2fb.png" height="64">
</picture>

# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/run-nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/run-nethermind-tests.yml)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)](https://discord.gg/GXJFaYk)
[![Follow us on Twitter](https://img.shields.io/twitter/follow/nethermindeth?style=social&label=Follow)](https://twitter.com/nethermindeth)

Nethermind is a high-performance, highly configurable full Ethereum protocol client built on .NET that runs on Linux, Windows, and macOS, and supports Clique, Aura, Ethash, and Proof-of-Stake consensus algorithms. Nethermind offers very fast sync speeds and support for external plug-ins. Enjoy reliable access to rich on-chain data thanks to high-performance JSON-RPC based on the Kestrel web server. Healthy node monitoring is secured with a Grafana dashboard and Seq enterprise logging.

## Documentation

Nethermind documentation can be found at [docs.nethermind.io](https://docs.nethermind.io).

### Supported networks

**`Mainnet`** **`Goerli`** **`Rinkeby`** **`Ropsten`** **`Sepolia`** **`Gnosis (xDai)`** **`Energy Web`** **`Volta`** **`Kovan`**

## Download and run

#### Using PPA

1. `sudo add-apt-repository ppa:nethermindeth/nethermind`
2. `sudo apt install nethermind`
3. Execute the launcher: `nethermind`
4. Execute the runner: `nethermind -c mainnet_pruned`

_Tested on Ubuntu Series: Focal, Bionic, Xenial, and Trusty_

#### Using Homebrew

1. `brew tap nethermindeth/nethermind`
2. `brew install nethermind`
3. Execute the launcher: `nethermind-launcher`
4. Execute the runner: `nethermind -c mainnet_pruned`

## Docker image

The official Docker images of Nethermind are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

### Get the digest of the Docker image

In case of any Docker image need to be updated in the repository, you can update the digest of these images as follows:

```sh
docker inspect --format='{{index .RepoDigests 0}}' <image_name>
```

The output must show the image digest, and then you can copy that output to the `FROM` tag in the Dockerfile.

## Build from source

### Prerequisites

#### Windows

-   [Install .NET](https://dotnet.microsoft.com/en-us/download?initial-os=windows)
-   For some versions of Windows, you may need to install [Visual C++ Redistributable](https://docs.microsoft.com/en-US/cpp/windows/latest-supported-vc-redist).

#### macOS

-   [Install .NET](https://dotnet.microsoft.com/en-us/download?initial-os=macos)
-   Install dependencies:

    ```sh
    brew install rocksdb gmp snappy lz4 zstd
    ```

-   _Apple silicon (M1) users only._ Create symlink for homebrew dependencies:

    ```sh
    sudo ln -s `find /opt/homebrew/Cellar/snappy -name "libsnappy.dylib"` /usr/local/lib/libsnappy.dylib
    ```

#### Ubuntu

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-ubuntu)
-   Install dependencies:

    ```sh
    sudo apt-get install libsnappy-dev libc6-dev libc6

    # Link libraries (only for Ubuntu 21.04 and later)
    amd64 architecture: sudo ln -s /usr/lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so
    arm64/aarch64 architecture: sudo ln -s /usr/lib/aarch64-linux-gnu/libdl.so.2 /usr/lib/aarch64-linux-gnu/libdl.so

    # Extra dependency for arm64/aarch64
    sudo apt-get install libgflags-dev
    ```

_Tested on Ubuntu 21.04, 20.04 and 18.04 LTS and 21.10_

#### Debian

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-debian)
-   Install dependencies:

    ```sh
    sudo apt-get install libsnappy-dev libc6-dev libc6
    ```

_Tested on Debian 10 (9 not working)_

#### CentOS

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-centos)
-   Install dependencies:

    ```sh
    sudo yum install -y glibc-devel bzip2-devel libzstd

    # Link libraries
    sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
    sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
    ```

_Tested on CentOS 8_

#### Fedora

-   [Install .NET](https://docs.microsoft.com/en-us/dotnet/core/install/linux-fedora)
-   Install dependencies:

    ```sh
    sudo yum install -y glibc-devel snappy libzstd

    # Link libraries
    sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0 && \
    sudo ln -s `find /usr/lib64/ -type f -name "libsnappy.so.1*"` /usr/lib64/libsnappy.so
    # also required for Fedora 35
    sudo ln -s `find /usr/lib64/ -type f -name "libdl.so.2*"` /usr/lib64/libdl.so
    ```

_Tested on Fedora 32_

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
