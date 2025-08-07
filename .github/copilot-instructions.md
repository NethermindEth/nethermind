# Copilot Instructions for Nethermind

## Repository Overview

Nethermind is an industry-leading Ethereum execution client built on .NET, designed for high-performance syncing and tip-of-chain processing. This enterprise-grade blockchain client features a modular architecture with plugin system support, serving multiple networks including Ethereum, Gnosis, Optimism, Base, Taiko, World Chain, Linea, and Energy Web.

**Repository Characteristics:**
- **Size:** Large-scale enterprise codebase with 100+ C# projects
- **Language:** C# with .NET 9.0 target framework (LangVersion 13.0)
- **Architecture:** Modular design with plugin-based extensibility
- **Type:** High-performance blockchain execution client
- **License:** LGPL-3.0-only

## Build Requirements and Setup

### Prerequisites

**CRITICAL:** Always install .NET SDK 9.0.x before building. The project requires specific .NET version compatibility.

```bash
# Install .NET 9.0 (if not available via package manager)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0
export PATH="$HOME/.dotnet:$PATH"

# Verify installation
dotnet --version  # Should show 9.0.x
```

### Build Instructions

**Complete build sequence (always follow this order):**

```bash
# 1. Navigate to main source directory
cd src/Nethermind

# 2. Build main solution (takes ~4 minutes)
dotnet build Nethermind.slnx -c Release

# 3. Build Ethereum Foundation tests (if needed)
dotnet build EthereumTests.slnx -c Release

# 4. Build benchmarks (if needed)
dotnet build Benchmarks.slnx -c Release
```

### Testing

**Run tests in this order to ensure dependencies are built:**

```bash
# Core functionality tests (fast, ~15 seconds)
dotnet test Nethermind.Core.Test/Nethermind.Core.Test.csproj

# Full Nethermind test suite
dotnet test Nethermind.slnx -c Release

# Ethereum Foundation tests (comprehensive, takes longer)
dotnet test EthereumTests.slnx -c Release
```

### Code Formatting and Validation

**ALWAYS run before creating PRs:**

```bash
# Check code formatting (required by CI)
dotnet format whitespace src/Nethermind/ --folder --verify-no-changes

# Fix formatting issues
dotnet format whitespace src/Nethermind/ --folder
```

### Running the Application

```bash
# From repository root
cd src/Nethermind/Nethermind.Runner

# Run with mainnet configuration
dotnet run -c release -- -c mainnet --data-dir path/to/data/dir

# Debug mode
dotnet run -c debug -- -c mainnet
```

## Project Architecture and Layout

### Solution Structure

The codebase is organized into three main solutions:

- **`src/Nethermind/Nethermind.slnx`** - Main application and libraries
- **`src/Nethermind/EthereumTests.slnx`** - Ethereum Foundation test suite
- **`src/Nethermind/Benchmarks.slnx`** - Performance benchmarking tools

### Key Directories

```
src/Nethermind/
├── Nethermind.Runner/           # Main executable entry point
├── Nethermind.Core/             # Core types and utilities
├── Nethermind.Blockchain/       # Block processing logic
├── Nethermind.Consensus.*       # Consensus mechanisms (Ethash, AuRa, Clique)
├── Nethermind.Synchronization/  # Node synchronization
├── Nethermind.Network*/         # P2P networking stack
├── Nethermind.JsonRpc/          # JSON-RPC API layer
├── Nethermind.State/            # State management
├── Nethermind.TxPool/           # Transaction pool
├── Nethermind.Evm/              # Ethereum Virtual Machine
└── *Test/                       # Test projects (suffix pattern)
```

### Configuration Files

- **`global.json`** - .NET SDK version requirement (9.0.2)
- **`Directory.Build.props`** - MSBuild properties and compilation settings
- **`.editorconfig`** - Code style rules (enforced in CI)
- **`nuget.config`** - NuGet package source configuration
- **`src/Nethermind/Directory.Build.props`** - Project-specific build properties

### Critical Build Dependencies

1. **TreatWarningsAsErrors:** Project configured to treat warnings as errors
2. **InvariantGlobalization:** Enabled for consistent behavior across locales
3. **UseArtifactsOutput:** Build outputs go to `artifacts/` directory
4. **Git submodules:** May be required for some test suites (`git clone --recursive`)

## Continuous Integration Validation

The project uses GitHub Actions with these key workflows:

### Pre-commit Checks
```bash
# Replicate CI formatting check locally
dotnet format whitespace src/Nethermind/ --folder --verify-no-changes

# Replicate CI build checks
dotnet build src/Nethermind/Nethermind.slnx -c Release
dotnet build src/Nethermind/Nethermind.slnx -c Debug
```

### Test Validation
```bash
# Core test matrix (replicates CI)
dotnet test Nethermind.Core.Test/Nethermind.Core.Test.csproj
dotnet test EthereumTests.slnx -c Release  # Comprehensive test suite
```

## Common Development Workflows

### Making Code Changes

1. **Always run formatting first:** `dotnet format whitespace src/Nethermind/ --folder`
2. **Build incrementally:** Start with specific project if working on single component
3. **Test locally:** Run relevant test project before full suite
4. **Validate CI requirements:** Run formatting check before committing

### Performance Considerations

- **Full build time:** ~4 minutes for Release configuration
- **Test execution:** Core tests ~15 seconds, full suite several minutes
- **Memory usage:** Large solution requires adequate system memory
- **Parallel builds:** MSBuild automatically parallelizes where possible

### File Headers

All source files must include this header:
```csharp
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```

## Troubleshooting Common Issues

### .NET SDK Version Mismatch
```
Error: A compatible .NET SDK was not found. Requested SDK version: 9.0.2
```
**Solution:** Install .NET 9.0.x SDK as shown in prerequisites section.

### Build Timeout in CI
**Cause:** Full builds take 4+ minutes
**Solution:** Use `timeout: 600` for build commands in automation

### Memory Issues During Build
**Cause:** Large solution with many projects
**Solution:** Ensure adequate system memory (8GB+ recommended)

### Code Formatting Failures
```
Error: Fix whitespace formatting by running: dotnet format whitespace
```
**Solution:** Run `dotnet format whitespace src/Nethermind/ --folder` before committing

## Docker Development

```bash
# Build Docker image directly from repository
docker build https://github.com/nethermindeth/nethermind.git -t nethermind

# Run containerized
docker run -d nethermind -c mainnet
```

## Trust These Instructions

These instructions are validated against the current codebase. Only search for additional information if:
- Instructions are incomplete for your specific task
- You encounter errors not covered in troubleshooting
- Working with components not mentioned in the architecture section

When in doubt, start with the build sequence and test a small component first before attempting larger changes.