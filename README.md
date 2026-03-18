<h1 align="center">Nethermind Ethereum Client</h1>

<p align="center">
  <a href="https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml"><img src="https://github.com/nethermindeth/nethermind/actions/workflows/nethermind-tests.yml/badge.svg" alt="Tests"></a>
  <a href="https://x.com/nethermind"><img src="https://img.shields.io/twitter/follow/nethermind?style=social&label=Follow%20us" alt="Follow on X"></a>
  <a href="https://discord.gg/GXJFaYk"><img src="https://img.shields.io/discord/629004402170134531?style=social&logo=discord" alt="Discord"></a>
  <a href="https://github.com/nethermindeth/nethermind/discussions"><img src="https://img.shields.io/github/discussions/nethermindeth/nethermind?style=social" alt="GitHub Discussions"></a>
  <a href="https://www.gitpoap.io/gh/NethermindEth/nethermind"><img src="https://public-api.gitpoap.io/v1/repo/NethermindEth/nethermind/badge" alt="GitPOAPs"></a>
</p>

<p align="center">
  <img src="https://github.com/user-attachments/assets/bcc0fbeb-fd03-4b9f-9b19-c3790e1fe2fa" alt="Nethermind Client" width="100%">
</p>

<p align="center">
  <a href="https://docs.nethermind.io/get-started/installing-nethermind"><img src="https://img.shields.io/badge/Install-F0921E?style=flat-square&logoColor=white" alt="Install"></a>&nbsp;
  <a href="https://docs.nethermind.io"><img src="https://img.shields.io/badge/Docs-1A1A2E?style=flat-square&logoColor=white" alt="Docs"></a>&nbsp;
  <a href="./CONTRIBUTING.md"><img src="https://img.shields.io/badge/Contribute-1A1A2E?style=flat-square&logoColor=white" alt="Contributing"></a>
</p>

---

## Overview

Nethermind Client is a high-performance Ethereum execution client built on .NET. Compatible with all consensus layer implementations that support the [Engine API](https://github.com/ethereum/execution-apis/tree/main/src/engine). It provides fast sync, high-throughput JSON-RPC, and a plugin system for extending the client without forking. In production since 2017.

Runs on Linux, Windows, and macOS (x64 and ARM).

Supported networks: Ethereum, Gnosis Chain, OP Stack (Optimism, Base, World Chain), Taiko, and Linea.

## Capabilities

Nethermind Client connects operators to the Ethereum network via JSON-RPC over HTTP, WebSocket, and IPC. Snap sync, enabled by default, reaches the chain tip up to 10x faster than traditional fast sync. Node health and performance are exposed through a built-in UI and Prometheus metrics.

1. **Performance:** The EVM is optimized for low-overhead block processing: direct opcode dispatch, hardware-accelerated bitwise operations, and zero heap allocation on the execution stack. A parallel pre-execution system warms state reads before a block's main loop, cutting block processing time roughly in half. 

2. **Modularity:** Every component of Nethermind Client is independently extendable without forking the codebase. The plugin system lets teams add consensus algorithms, transaction types, network protocols, and RPC namespaces through a .NET assembly that loads on startup. Nethermind uses this same system internally for L2 network support and health checks.

3. **Client diversity:** The Ethereum protocol becomes more resilient when no single node implementation dominates. A bug in any one implementation cannot cause the network to finalize a bad block if multiple independent clients are running. 

4. **L2 and rollup native:** Each supported L2 network is implemented as a plugin so the core stays untouched. For OP Stack operators, a rollup node is built directly into the client, fully replacing the separate `op-node` and cutting services from two down to one.

5. **ZK-readiness:** ZK proving is being built directly into the production execution client. Execution witness capture, stateless block replay, and a minimal EVM binary are complete. See the [ZK roadmap](https://www.nethermind.io/blog/road-to-zk-implementation-nethermind-clients-path-to-proofs) for current status.

6. **Open source:** Nethermind Client is licensed under LGPL-3.0 and GPL-3.0. The full source is published on GitHub and the project welcomes contributions from developers and organizations across the Ethereum ecosystem.

## Getting Started

Standalone release builds are available on [GitHub Releases](https://github.com/nethermindeth/nethermind/releases). For minimum and recommended hardware requirements, see [System requirements](https://docs.nethermind.io/get-started/system-requirements).

Migrating from Geth? Nethermind supports the same JSON-RPC API. See the [migration guide](https://docs.nethermind.io/get-started/migrating-from-geth).

### Package managers

**Linux** (Debian/Ubuntu)

```bash
sudo add-apt-repository ppa:nethermindeth/nethermind
sudo apt-get install nethermind
```

**Windows**

```powershell
winget install --id Nethermind.Nethermind
```

**macOS**

```bash
brew tap nethermindeth/nethermind
brew install nethermind
```

Once installed:

```bash
nethermind -c mainnet --data-dir path/to/data/dir
```

For full setup instructions, see [Running a node](https://docs.nethermind.io/get-started/running-node). To spin up Nethermind alongside a consensus client in one command, see [Sedge](https://docs.sedge.nethermind.io/docs/networks/mainnet).

### Docker

Official images are available on [Docker Hub](https://hub.docker.com/r/nethermind/nethermind). For available tags and usage, see [Docker container setup](https://docs.nethermind.io/get-started/installing-nethermind#docker-container).

### Building from source

Requires .NET SDK 10 or later. See [Building from source](https://docs.nethermind.io/developers/building-from-source) for instructions on building standalone binaries and Docker images from the repository.

## Developer Guide

### Using Nethermind as a library

Individual Nethermind components (the EVM, trie, RLP serializer, and cryptographic primitives) are available as NuGet packages. Use [Nethermind.ReferenceAssemblies](https://www.nuget.org/packages/Nethermind.ReferenceAssemblies) to build plugins against the Nethermind API without cloning the source. 

### Plugin development

Nethermind Client's plugin system lets teams extend the client without touching the core. This is the same system used internally for L2 network support, health checks, Shutter, and more. Plugins are .NET assemblies loaded on startup and can provide:

- New consensus engines (`IConsensusPlugin`, `IConsensusWrapperPlugin`)
- Custom transaction types and RLP decoders (`InitTxTypesAndRlpDecoders`)
- New P2P protocol handlers (`InitNetworkProtocol`)
- Additional JSON-RPC namespaces (`InitRpcModules`)
- Autofac DI modules and initialization steps

See the [plugin development guide](https://docs.nethermind.io/developers/plugins) for a full lifecycle walkthrough, configuration auto-mapping, and working examples. Join the [plugin development channel](https://discord.gg/K8MdZT3keK) on Discord.

## Getting Help

Check the [documentation](https://docs.nethermind.io) first. If the answer is not there:

- [Discord](https://discord.gg/GXJFaYk) for community support
- [GitHub Discussions](https://github.com/nethermindeth/nethermind/discussions/new) for questions and proposals
- [GitHub Issues](https://github.com/nethermindeth/nethermind/issues/new/choose) for confirmed bugs

**Migrating from Geth?** Nethermind supports the same JSON-RPC API. See the [migration guide](https://docs.nethermind.io/get-started/migrating-from-geth).

## Contributing

Before working on a feature or fix, read our [contributing guidelines](https://github.com/NethermindEth/nethermind/blob/master/CONTRIBUTING.md) to avoid wasted or duplicate effort.

## Security

If you believe you have found a security vulnerability, report it as described in our [security policy](https://github.com/NethermindEth/nethermind/blob/master/SECURITY.md). Do not open a public issue.

## License

Nethermind is open-source software licensed under [LGPL-3.0](https://github.com/NethermindEth/nethermind/blob/master/LICENSE-LGPL) and [GPL-3.0](https://github.com/NethermindEth/nethermind/blob/master/LICENSE-GPL). By using this project, you agree to abide by the licenses and [additional terms](https://nethermindeth.github.io/NethermindEthereumClientTermsandConditions/).