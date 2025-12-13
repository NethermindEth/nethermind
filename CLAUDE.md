# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

Nethermind is an industry-leading Ethereum execution client built on .NET 9.0, designed for high-performance syncing and tip-of-chain processing. It features a modular architecture with a plugin system, supporting multiple networks including Ethereum, Gnosis, Optimism, Base, Taiko, World Chain, Linea, and Energy Web.

**Key Characteristics:**
- Large-scale C# codebase with 100+ projects
- Target framework: .NET 9.0 (C# 13)
- License: LGPL-3.0-only
- Modular plugin-based architecture

## Essential Build Commands

### Prerequisites
- .NET SDK 9.0.2 or later (required by `global.json`)
- Git with submodules: `git clone --recursive https://github.com/nethermindeth/nethermind.git`

### Build Commands
All build commands run from `src/Nethermind/`:

```bash
# Navigate to source directory
cd src/Nethermind

# Build main solution (~4 minutes)
dotnet build Nethermind.slnx -c Release

# Build for debugging
dotnet build Nethermind.slnx -c Debug

# Build Ethereum Foundation tests (when needed)
dotnet build EthereumTests.slnx -c Release

# Build benchmarks (when needed)
dotnet build Benchmarks.slnx -c Release
```

### Testing Commands

```bash
# From src/Nethermind/ directory

# Run single test project (fast, ~15 seconds)
dotnet test Nethermind.Core.Test/Nethermind.Core.Test.csproj

# Run all Nethermind tests
dotnet test Nethermind.slnx -c Release

# Run Ethereum Foundation test suite
dotnet test EthereumTests.slnx -c Release

# Run specific test class or method
dotnet test <project>.csproj --filter "FullyQualifiedName~ClassName"
```

### Code Formatting

**CRITICAL: Always run before committing. CI enforces this.**

```bash
# Check formatting (what CI runs)
dotnet format whitespace src/Nethermind/ --folder --verify-no-changes

# Fix formatting issues
dotnet format whitespace src/Nethermind/ --folder
```

### Running the Application

```bash
# From repository root
cd src/Nethermind/Nethermind.Runner

# Run mainnet
dotnet run -c release -- -c mainnet --data-dir path/to/data/dir

# Debug mode
dotnet run -c debug -- -c mainnet
```

## Architecture Overview

### Three Solution Structure

The codebase is organized into three independent solutions located in `src/Nethermind/`:

1. **Nethermind.slnx** - Main execution client with all core functionality
2. **EthereumTests.slnx** - Ethereum Foundation test suite compliance
3. **Benchmarks.slnx** - Performance benchmarking tools

### Core Architecture Layers

**Entry Point and Initialization:**
- `Nethermind.Runner/` - Application entry point, startup orchestration
- `Nethermind.Init/` - Initialization logic, memory management, metrics

**API and Extension System:**
- `Nethermind.Api/` - Core API interfaces (`INethermindApi`, `IApiWithNetwork`, `IApiWithStores`)
- `Nethermind.Api/Extensions/` - Plugin system interfaces (`INethermindPlugin`, `IConsensusPlugin`)
- Plugins implement lifecycle hooks: `InitTxTypesAndRlpDecoders()`, `Init()`, `InitNetworkProtocol()`, `InitRpcModules()`

**Consensus Layer:**
- `Nethermind.Consensus.*/` - Pluggable consensus mechanisms:
  - `Nethermind.Consensus.Ethash/` - Proof of Work
  - `Nethermind.Consensus.AuRa/` - Authority Round (Gnosis)
  - `Nethermind.Consensus.Clique/` - Proof of Authority
- `Nethermind.Merge.Plugin/` - Proof of Stake (post-merge Ethereum)

**Blockchain Core:**
- `Nethermind.Core/` - Fundamental types (`Block`, `BlockHeader`, `Transaction`, `TransactionReceipt`)
- `Nethermind.Blockchain/` - Block processing, chain management, validators
- `Nethermind.Evm/` - Ethereum Virtual Machine implementation
- `Nethermind.Evm.Precompiles/` - Precompiled contracts
- `Nethermind.Specs/` - Network specifications and hard fork rules

**State and Storage:**
- `Nethermind.State/` - World state management, accounts, contract storage
- `Nethermind.Trie/` - Merkle Patricia Trie implementation
- `Nethermind.Db/` - Database abstraction layer
- `Nethermind.Db.Rocks/` - RocksDB implementation (primary storage backend)

**Networking:**
- `Nethermind.Network/` - P2P protocol implementation (DevP2P)
- `Nethermind.Network.Discovery/` - Peer discovery
- `Nethermind.Network.Dns/` - DNS-based node discovery
- `Nethermind.Network.Enr/` - Ethereum Node Records
- `Nethermind.Synchronization/` - Block synchronization strategies (fast sync, snap sync, beam sync)

**Transaction Management:**
- `Nethermind.TxPool/` - Transaction pool management, validation, sorting
- Custom transaction types registered via `INethermindApi.RegisterTxType<T>()`

**RPC and External Interface:**
- `Nethermind.JsonRpc/` - JSON-RPC API server
- `Nethermind.Facade/` - High-level API facades for external interaction

**Serialization:**
- `Nethermind.Serialization.Rlp/` - RLP (Recursive Length Prefix) encoding
- `Nethermind.Serialization.Ssz/` - SSZ (Simple Serialize) for consensus layer
- `Nethermind.Serialization.Json/` - JSON serialization

**Network-Specific Implementations:**
- `Nethermind.Taiko/` - Taiko L2 support
- `Nethermind.Xdc/` - XDC Network support
- `Nethermind.Flashbots/` - MEV-Boost integration

### Plugin System Architecture

Plugins extend Nethermind functionality through the `INethermindPlugin` interface:

```csharp
public interface INethermindPlugin
{
    string Name { get; }
    void InitTxTypesAndRlpDecoders(INethermindApi api);
    Task Init(INethermindApi nethermindApi);
    Task InitNetworkProtocol();
    Task InitRpcModules();
    bool MustInitialize => false;
    bool Enabled { get; }
}
```

Consensus plugins implement `IConsensusPlugin` which extends `IBlockProducerFactory` and `IBlockProducerRunnerFactory`.

Examples: `Nethermind.Merge.Plugin`, `Nethermind.ExternalSigner.Plugin`, `Nethermind.UPnP.Plugin`

### Configuration System

Configuration uses strongly-typed interfaces inheriting from `IConfig`:
- Each module has `I<Module>Config` interface and `<Module>Config` implementation
- Examples: `ITxPoolConfig`, `IDbConfig`, `IInitConfig`
- Access via `INethermindApi.Config<T>()` where `T : IConfig`

## Code Style and Conventions

**Critical Requirements:**
- All code MUST follow `.editorconfig` rules (enforced by CI)
- Use C# 13 features (latest language version)
- All files require SPDX header:
  ```csharp
  // SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
  // SPDX-License-Identifier: LGPL-3.0-only
  ```

**Strongly Preferred Patterns:**
- Use file-scoped namespaces (but match existing file style if present)
- Pattern matching and switch expressions over traditional control flow
- `nameof()` instead of string literals for member references
- `is null` / `is not null` instead of `== null` / `!= null`
- `?.` null-conditional operator where applicable
- Trust null annotations - don't add redundant null checks
- `ObjectDisposedException.ThrowIf` for disposal checks
- Add tests to existing test files rather than creating new ones
- XML doc comments for all public APIs with proper structure:
  - Use `<summary>` tag for brief description (one sentence preferred)
  - Use `<param>` tag for each parameter with concise description
  - Use `<returns>` tag to describe the return value
  - Use `<remarks>` tag (optional) for additional implementation details, behavior notes, or important context when needed
- Code comments explain "why", not "what"

**Prohibited Patterns:**
- Do NOT use `#region`
- Do NOT use "Act", "Arrange", "Assert" comments in tests
- Do NOT leave tests commented out or disabled unless previously disabled
- Do NOT change `global.json`, `package.json`, `NuGet.config` unless explicitly requested

**Performance Focus:**
- Prefer low-allocation code patterns
- Consider performance implications in high-throughput paths

## Build Configuration

Key settings from `Directory.Build.props`:
- `TreatWarningsAsErrors`: true (warnings block builds)
- `InvariantGlobalization`: true (consistent cross-locale behavior)
- `UseArtifactsOutput`: true (outputs to `artifacts/` directory)
- `TargetFramework`: net9.0
- `LangVersion`: 13.0

## Testing Requirements

**Before committing code:**
1. Your changes MUST compile
2. New and existing related tests MUST pass
3. Formatting check MUST pass: `dotnet format whitespace src/Nethermind/ --folder --verify-no-changes`
4. Report if unable to verify build/test success

**When writing tests:**
- Use filters and verify test counts to ensure tests actually ran
- Copy style from nearby test files for naming and capitalization
- Ensure new code files are listed in `.csproj` if other files in the folder are listed

## Common File Locations

- Solution files: `src/Nethermind/*.slnx`
- Main entry point: `src/Nethermind/Nethermind.Runner/Program.cs`
- Core types: `src/Nethermind/Nethermind.Core/`
- API definitions: `src/Nethermind/Nethermind.Api/`
- Plugin interfaces: `src/Nethermind/Nethermind.Api/Extensions/`
- Global build config: `Directory.Build.props`, `global.json`
- Editor config: `.editorconfig`

## Branch Naming Convention

Use `kebab-case` or `snake_case`, all lowercase. Pattern: `[project/]type/[issue-]description`

Examples:
- `feature/1234-issue-title`
- `shanghai/feature/1234-issue-title`
- `fix/1234-bug-description`
- `shanghai/refactor/title`
