<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://github.com/nethermindeth/nethermind/assets/337518/3e3b3c06-9cf3-4364-a774-158e649588cc">
    <source media="(prefers-color-scheme: light)" srcset="https://github.com/nethermindeth/nethermind/assets/337518/d1cc365c-6045-409f-a961-18d22ddb2535">
    <img alt="Nethermind" src="https://github.com/nethermindeth/nethermind/assets/337518/d1cc365c-6045-409f-a961-18d22ddb2535" height="64">
  </picture>
</p>

# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml)
[![Follow us on X](https://img.shields.io/twitter/follow/nethermindeth?style=social&label=Follow%20us)](https://x.com/nethermindeth)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)](https://discord.gg/GXJFaYk)
[![GitHub Discussions](https://img.shields.io/github/discussions/nethermindeth/nethermind?style=social)](https://github.com/nethermindeth/nethermind/discussions)
[![GitPOAPs](https://public-api.gitpoap.io/v1/repo/NethermindEth/nethermind/badge)](https://www.gitpoap.io/gh/NethermindEth/nethermind)

The Nethermind Ethereum execution client, built on .NET, delivers industry-leading performance in syncing and tip-of-chain processing. With its modular design and plugin system, it offers extensibility and features for new chains. As one of the most adopted execution clients on Ethereum, Nethermind plays a crucial role in enhancing the diversity and resilience of the Ethereum ecosystem.

## Documentation

Nethermind documentation is available at [docs.nethermind.io](https://docs.nethermind.io).

### Supported networks

**Ethereum** · **Gnosis** · **Optimism** · **Base** · **Taiko** · **World Chain** · **Linea** · **Energy Web**

## Installing

The standalone release builds are available on [GitHub Releases](https://github.com/nethermindeth/nethermind/releases).

### Package managers

- **Linux**

  On Debian-based distros, Nethermind can be installed via Launchpad PPA:

  ```bash
  sudo add-apt-repository ppa:nethermindeth/nethermind
  # If command not found, run
  # sudo apt-get install software-properties-common

  sudo apt-get install nethermind
  ```

- **Windows**

  On Windows, Nethermind can be installed via Windows Package Manager:

  ```powershell
  winget install --id Nethermind.Nethermind
  ```

- **macOS**

  On macOS, Nethermind can be installed via Homebrew:

  ```bash
  brew tap nethermindeth/nethermind
  brew install nethermind
  ```

Once installed, Nethermind can be launched as follows:

```bash
nethermind -c mainnet --data-dir path/to/data/dir
```

For further instructions, see [Running a node](https://docs.nethermind.io/get-started/running-node).

### Docker containers

The official Docker images of Nethermind are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind) and tagged as follows:

- `latest`: the latest version of Nethermind (the default tag)
- `latest-chiseled`: a rootless and chiseled image of the latest version of Nethermind
- `x.x.x`: a specific version of Nethermind
- `x.x.x-chiseled`: a rootless and chiseled image of the specific version of Nethermind

For more info, see [Installing Nethermind](https://docs.nethermind.io/get-started/installing-nethermind).

## Building from source

### Docker image

This is the easiest and fastest way to build Nethermind if you don't want to clone the Nethermind repo, deal with .NET SDK installation, and other configurations. Running the following simple command builds the Docker image, which is ready to run right after:

```bash
docker build https://github.com/nethermindeth/nethermind.git -t nethermind
```

For more info, see [Building Docker image](https://docs.nethermind.io/developers/building-from-source#building-docker-image).

### Standalone binaries

**Prerequisites**

Install the [.NET SDK](https://aka.ms/dotnet/download).

**Clone the repository**

```bash
git clone --recursive https://github.com/nethermindeth/nethermind.git
```

**Build and run**

```bash
cd nethermind/src/Nethermind/Nethermind.Runner
dotnet run -c release -- -c mainnet
```

**Test**

```bash
cd nethermind/src/Nethermind

# Run Nethermind tests
dotnet test Nethermind.slnx -c release

# Run Ethereum Foundation tests
dotnet test EthereumTests.slnx -c release
```

For more info, see [Building standalone binaries](https://docs.nethermind.io/developers/building-from-source#building-standalone-binaries).

## Contributing

Before you start working on a feature or fix, please read and follow our [contributing guidelines](./CONTRIBUTING.md) to help avoid any wasted or duplicate effort.

## Security

If you believe you have found a security vulnerability in our code, please report it to us as described in our [security policy](SECURITY.md).

## License

Nethermind is an open-source software licensed under the [LGPL-3.0](./LICENSE-LGPL). By using this project, you agree to abide by the license and [additional terms](https://nethermindeth.github.io/NethermindEthereumClientTermsandConditions/).
## Layer2 Compatibility Tips
Nethermind supports Layer2 like Optimism and Arbitrum out-of-the-box! Use --JsonRpc.Enabled=true for cross-layer RPC. 🚀
