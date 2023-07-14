<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://github.com/nethermindeth/nethermind/assets/337518/3e3b3c06-9cf3-4364-a774-158e649588cc">
    <source media="(prefers-color-scheme: light)" srcset="https://github.com/nethermindeth/nethermind/assets/337518/d1cc365c-6045-409f-a961-18d22ddb2535">
    <img alt="Nethermind" src="https://github.com/nethermindeth/nethermind/assets/337518/d1cc365c-6045-409f-a961-18d22ddb2535" height="64">
  </picture>
</p>

# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)](https://discord.gg/GXJFaYk)
[![Follow us on Twitter](https://img.shields.io/twitter/follow/nethermindeth?style=social&label=Follow)](https://twitter.com/nethermindeth)
[![Ask on Discourse](https://img.shields.io/discourse/posts?style=social&label=Community&logo=discourse&server=https%3A%2F%2Fcommunity.nethermind.io)](https://community.nethermind.io/c/nethermind-client)
[![GitPOAPs](https://public-api.gitpoap.io/v1/repo/NethermindEth/nethermind/badge)](https://www.gitpoap.io/gh/NethermindEth/nethermind)

Nethermind is a high-performance, highly configurable full Ethereum protocol execution client built on .NET that runs on Linux, Windows, and macOS, and supports Clique, Aura, and Ethash. Nethermind offers very fast sync speeds and support for external plugins. Enjoy reliable access to rich on-chain data thanks to high-performance JSON-RPC based on the Kestrel web server. Healthy node monitoring is secured with Grafana analytics and Seq logging.

## Documentation

Nethermind documentation is available at [docs.nethermind.io](https://docs.nethermind.io).

### Supported networks

**`Mainnet`** **`Goerli`** **`Sepolia`** **`Gnosis (xDai)`** **`Energy Web`** **`Volta`**

## Download and run

Release builds are available on the [Releases page](https://github.com/nethermindeth/nethermind/releases) and at [downloads.nethermind.io](https://downloads.nethermind.io).

### On Linux

#### Prerequisites

- #### Ubuntu / Debian

  ```sh
  sudo apt-get install libsnappy-dev
  ```

- #### CentOS / Fedora

  ```sh
  sudo dnf install -y snappy
  sudo ln -s `find /usr/lib64/ -type f -name "libbz2.so.1*"` /usr/lib64/libbz2.so.1.0
  ```

#### Install using PPA

1. `sudo add-apt-repository ppa:nethermindeth/nethermind` \
   If command not found: `sudo apt-get install software-properties-common`
2. `sudo apt-get install nethermind`
3. To run directly: `nethermind -c mainnet` \
   or with the assistant: `nethermind`

### On Windows

#### Prerequisites

Install [Visual C++ Redistributable](https://aka.ms/vcredist):
```
winget install Microsoft.VCRedist.2015+.x64
```

#### Install using Windows Package Manager

1. `winget install nethermind`
2. To run directly: `nethermind.runner.exe -c mainnet` \
   or with the assistant: `nethermind.launcher.exe` 

### On macOS

#### Prerequisites

```sh
brew install lz4 snappy zstd
```

#### Install using Homebrew

1. `brew tap nethermindeth/nethermind`
2. `brew install nethermind`
3. To run directly: `nethermind -c mainnet` \
   or with the assistant: `nethermind-launcher`

## Docker image

The official Docker images of Nethermind are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind).

### Get the digest of the Docker image

In case of any Docker image need to be updated in the repository, you can update the digest of these images as follows:

```sh
docker inspect --format='{{index .RepoDigests 0}}' <image_name>
```

The output should show the image digest, and then you can copy that to the `FROM` tag in the Dockerfile.

## Building from source

### Prerequisites

Install [.NET SDK](https://dotnet.microsoft.com/en-us/download)

### Clone the repository

```sh
git clone https://github.com/nethermindeth/nethermind --recursive
```

### Build and run

```sh
cd nethermind/src/Nethermind/Nethermind.Runner
dotnet run -c release -- -c mainnet
```

### Test

```sh
cd nethermind/src/Nethermind

# Run Nethermind tests:
dotnet test Nethermind.sln -c release

# Run Ethereum Foundation tests:
dotnet test EthereumTests.sln -c release
```

## Contributing

BEFORE you start work on a feature or fix, please read and follow our [contribution guide](https://github.com/nethermindeth/nethermind/blob/master/CONTRIBUTING.md) to help avoid any wasted or duplicate effort.

## Security 

If you believe you have found a security vulnerability in our code, please report it to us as described in our [security policy](SECURITY.md).

## License

Nethermind is an open-source software licensed under the [LGPL-3.0](https://github.com/nethermindeth/nethermind/blob/master/LICENSE-LGPL).
