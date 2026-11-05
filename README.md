# Nethermind Ethereum client

[![Tests](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg)](https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml)
[![Follow us on X](https://img.shields.io/twitter/follow/nethermind?style=social&label=Follow%20us)](https://x.com/nethermind)
[![Chat on Discord](https://img.shields.io/discord/629004402170134531?style=social&logo=discord)][discord]
[![GitHub Discussions](https://img.shields.io/github/discussions/nethermindeth/nethermind?style=social)][discussions]

<p align="center">
  <img src="https://github.com/user-attachments/assets/7c077f6c-4a62-42e2-9ef1-abc580bb2413" alt="Nethermind client" width="100%">
</p>

## Overview

Nethermind is a high-performance Ethereum execution client built on .NET. It provides fast sync, high-throughput JSON-RPC, and a plugin system for extending the client without forking. In production since 2017.

Runs on Linux, Windows, and macOS.

### Supported networks

**Ethereum** · **Gnosis** · **Taiko** · **Linea** · **Energy Web**

### Documentation

Nethermind documentation is available at [docs.nethermind.io][docs].

## Capabilities

Nethermind connects operators to the Ethereum network via JSON-RPC over HTTP, WebSocket, and IPC. Snap sync, enabled by default, reaches the chain tip up to 10x faster than traditional fast sync. Node health and performance are exposed through a built-in UI and Prometheus metrics.

- **Performance:** The EVM is optimized for low-overhead block processing: direct opcode dispatch, hardware-accelerated bitwise operations, and zero heap allocation on the execution stack. A parallel pre-execution system warms state reads before a block's main loop, cutting block processing time roughly in half.

- **Modularity:** Every component of the Nethermind is independently extendable without forking the codebase. The plugin system lets teams add consensus algorithms, transaction types, network protocols, and RPC namespaces through a .NET assembly that loads on startup. Nethermind uses this same system internally for L2 network support and health checks.

- **Client diversity:** The Ethereum protocol becomes more resilient when no single node implementation dominates. A bug in any one implementation cannot cause the network to finalize a bad block if multiple independent clients are running.

- **L2 and rollup native:** Each supported L2 network is implemented as a plugin, so the core stays untouched.

- **ZK-readiness:** ZK proving is being built directly into the production execution client. Execution witness capture, stateless block replay, and a minimal EVM binary are complete. See the [ZK roadmap](https://www.nethermind.io/blog/road-to-zk-implementation-nethermind-clients-path-to-proofs) for current status.

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
  winget install --id Nethermind.Nethermind -e
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

Install [.NET SDK](https://get.dot.net) 10 or later.

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

Pull-request opcode benchmark comparisons use the immutable merge revision, keeping
the workflow's helper scripts and measured code on the same revision. The comparison
job installs the latest serviced .NET 10 SDK for its file-based helper dependencies.

## Plugin development

Nethermind's plugin system lets teams extend the client without touching the core. This is the same system used internally for L2 network support, health checks, Shutter, and more. Plugins are loaded on startup and can provide:

- New consensus engines
- Custom transaction types and RLP decoders
- New P2P protocol handlers
- Additional JSON-RPC namespaces

See the [plugin development guide](https://docs.nethermind.io/developers/plugins) for a full lifecycle walkthrough, configuration auto-mapping, and working examples. Join the [plugin development channel](https://discord.gg/K8MdZT3keK) on Discord.

Read-only execution environments use geth's precompile override behavior by default:
any account override replaces the native precompile with the account's code.
An extension can register `PrecompileOverridePolicy(disableOnAccountOverride: false)`
from `Nethermind.State.OverridableEnv` to retain native dispatch while still applying
storage, balance, nonce and code changes to the overridden state. Native dispatch
and the plain bytecode/delegation view share the overridden bytes and are cached
once per override. Code introspection sees those bytes; calling the native address
does not follow an overridden EIP-7702 designator, while an ordinary account
delegating to it executes its visible code without following delegation twice.
Explicit precompile moves remain separate. Ordinary VM outcomes marked `ShouldRevert` preserve their
original revert bytes through `eth_call` and `eth_estimateGas`, even when an
extension does not also set the redundant `Revert` exception marker.

Decorators implementing `IOverridableCodeInfoRepository` must forward
`DisablePrecompileOnAccountOverride`. The override layer uses this policy before
allocating unused replacement code wrappers for non-code account overrides.
RPC tests exercise both default and container-registered policies with empty, balance,
nonce, code, state and state-diff account overrides.

Chains can independently select fee semantics for `eth_call` by registering the
immutable `Nethermind.Facade.RpcCallExecutionPolicy`. Its default values preserve
existing Ethereum/geth behavior. Setting `chargeFees: false` skips gas purchase,
monetary refunds and fee distribution without creating balances, changing gas
metering, or bypassing value affordability. The independent
`validateLegacyGasPriceAgainstBaseFee` flag does not disable typed fee-cap checks.
These options apply only to restoring calls, not estimates, traces, simulated
blocks or consensus execution. `PayFees` overrides receive `ExecutionOptions`;
forward them to the base implementation and use `ShouldChargeTransactionFees`
for monetary side effects without skipping unrelated execution semantics.

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
