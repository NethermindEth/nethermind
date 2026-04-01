# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml)
[![Follow us on X](https://img.shields.io/twitter/follow/nethermind?style=social&label=Follow%20us)](https://x.com/nethermind)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)][discord]
[![GitHub Discussions](https://img.shields.io/github/discussions/nethermindeth/nethermind?style=social)][discussions]
[![GitPOAPs](https://public-api.gitpoap.io/v1/repo/NethermindEth/nethermind/badge)](https://www.gitpoap.io/gh/NethermindEth/nethermind)

<p align="center">
  <img src="https://github.com/user-attachments/assets/bcc0fbeb-fd03-4b9f-9b19-c3790e1fe2fa" alt="Nethermind client" width="100%">
</p>

## Overview

Nethermind is a high-performance Ethereum execution client built on .NET. It provides fast sync, high-throughput JSON-RPC, and a plugin system for extending the client without forking. In production since 2017.

Runs on Linux, Windows, and macOS.

### Supported networks

**Ethereum** · **Gnosis** · **Optimism** · **Base** · **Taiko** · **World Chain** · **Linea** · **Energy Web**

### Documentation

Nethermind documentation is available at [docs.nethermind.io][docs].

## Capabilities

Nethermind connects operators to the Ethereum network via JSON-RPC over HTTP, WebSocket, and IPC. Snap sync, enabled by default, reaches the chain tip up to 10x faster than traditional fast sync. Node health and performance are exposed through a built-in UI and Prometheus metrics.

1. **Performance:** The EVM is optimized for low-overhead block processing: direct opcode dispatch, hardware-accelerated bitwise operations, and zero heap allocation on the execution stack. A parallel pre-execution system warms state reads before a block's main loop, cutting block processing time roughly in half.

2. **Modularity:** Every component of the Nethermind is independently extendable without forking the codebase. The plugin system lets teams add consensus algorithms, transaction types, network protocols, and RPC namespaces through a .NET assembly that loads on startup. Nethermind uses this same system internally for L2 network support and health checks.

3. **Client diversity:** The Ethereum protocol becomes more resilient when no single node implementation dominates. A bug in any one implementation cannot cause the network to finalize a bad block if multiple independent clients are running.

4. **L2 and rollup native:** Each supported L2 network is implemented as a plugin, so the core stays untouched. For OP Stack operators, a rollup node is built directly into the client, fully replacing the separate `op-node` and cutting services from two down to one.

5. **ZK-readiness:** ZK proving is being built directly into the production execution client. Execution witness capture, stateless block replay, and a minimal EVM binary are complete. See the [ZK roadmap](https://www.nethermind.io/blog/road-to-zk-implementation-nethermind-clients-path-to-proofs) for current status.

## Getting started

Standalone release builds are available on [GitHub Releases](https://github.com/nethermindeth/nethermind/releases). For hardware requirements, see [System requirements](https://docs.nethermind.io/get-started/system-requirements).

Migrating from Geth? Nethermind supports the same JSON-RPC API. See the [migration guide](https://docs.nethermind.io/get-started/migrating-from-geth).

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

For full setup instructions, see [Running a node](https://docs.nethermind.io/get-started/running-node). To spin up Nethermind alongside a consensus client in one command, see [Sedge](https://docs.sedge.nethermind.io/docs/networks/mainnet).

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

Install [.NET SDK](https://aka.ms/dotnet/download) 10 or later.

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
dotnet test --solution Nethermind.slnx -c release

# Run Ethereum Foundation tests
dotnet test --solution EthereumTests.slnx -c release
```

For more info, see [Building standalone binaries](https://docs.nethermind.io/developers/building-from-source#building-standalone-binaries).

## Plugin development

Nethermind's plugin system lets teams extend the client without touching the core. This is the same system used internally for L2 network support, health checks, Shutter, and more. Plugins are loaded on startup and can provide:

- New consensus engines
- Custom transaction types and RLP decoders
- New P2P protocol handlers
- Additional JSON-RPC namespaces

See the [plugin development guide](https://docs.nethermind.io/developers/plugins) for a full lifecycle walkthrough, configuration auto-mapping, and working examples. Join the [plugin development channel](https://discord.gg/K8MdZT3keK) on Discord.

## Getting help

Check out the [docs][docs] first. If the answer is not there, see:

- [Discord](https://discord.gg/GXJFaYk) for community support
- [GitHub Discussions][discussions] for questions and proposals
- [GitHub Issues](https://github.com/nethermindeth/nethermind/issues) to report an issue

## Contributing

Before you start working on a feature or fix, please read and follow our [contributing guidelines](./CONTRIBUTING.md) to help avoid any wasted or duplicate effort.

## Security

If you believe you have found a security vulnerability in our code, please report it to us as described in our [security policy](SECURITY.md).

## License

Nethermind is an open-source software licensed under the [LGPL-3.0](./LICENSE-LGPL). By using this project, you agree to abide by the license and [additional terms](https://nethermindeth.github.io/NethermindEthereumClientTermsandConditions/).

[discord]: https://discord.gg/GXJFaYk
[discussions]: https://github.com/nethermindeth/nethermind/discussions
[docs]: https://docs.nethermind.io
