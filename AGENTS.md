# AGENTS instructions

This guide helps to get started with the Nethermind Ethereum execution client repository. It covers the project structure, how to build and test the code, and follow the PR workflow.

## Repo structure

- [src/Nethermind](./src/Nethermind/): The Nethermind codebase
- [tools](./tools/): Various servicing tools for testing, monitoring, etc.
- [scripts](./scripts/): The build scripts and stuff used by GitHub Actions workflows
- See [README.md](./README.md) for more info

## Coding guidelines and style

- Do follow the [CONTRIBUTING.md](./CONTRIBUTING.md) guidelines
- Do follow the [.editorconfig](./.editorconfig) rules
- Do prefer low-allocation code patterns
- Prefer the latest C# syntax and conventions
- Prefer file-scoped namespaces (for existing files, follow their style)
- Prefer pattern matching and switch expressions over the traditional control flow
- Use the `nameof` operator instead of string literals for member references
- Use `is null` and `is not null` instead of `== null` and `!= null`
- Use `?.` null-conditional operator where applicable
- Use the `ArgumentNullException.ThrowIfNull` method for null checks and other similar methods
- Use the `ObjectDisposedException.ThrowIf` method for disposal checks
- Use documentation comments for all public APIs with proper structure
- Avoid `var` when declaring variables, the only acceptable exceptions are very long type names e.g. nested generic types
- Consider performance implications in high-throughput paths
- Trust null annotations, do not add redundant null checks
- When fixing a bug, always add a regression test that fails without the fix and passes with it
- Add tests to existing test files rather than creating new ones
- When adding multiple, similar tests write one test with test cases (`[TestCase]` or `[TestCaseSource]`); never duplicate a test body across methods when only the inputs differ
- When adding a test, check if previous tests can be reused with new test case
- Code comments must explain _why_, not _what_
- **NEVER suggest using LINQ (`.Select()`, `.Where()`, `.Any()`, etc.) when a simple `foreach` or `for` loop would work.** LINQ has overhead and is less readable for simple iterations. Use LINQ only for complex queries where the declarative syntax significantly improves clarity.
- Keep changes minimal and focused: do not rename variables, reformat surrounding code, or refactor unrelated logic as part of a fix. Touch only what is necessary to solve the problem.
- Follow DRY: after making changes, review the result for duplicated logic. Extract repeated blocks (roughly 5+ lines) into shared methods, but do not over-extract trivial one-liners into their own methods.
- For EVM hot-path optimizations, prioritize maintainability and single-source-of-truth flow first; use `IFlag` specialization (e.g., `TFast : IFlag`) when it meaningfully reduces duplication and complexity, but do not force one exact pattern.
- In generic types, move methods that do not depend on the type parameter to a non-generic base class or static helper to avoid redundant JIT instantiations per closed type.
- Do not use the `#region` and `#endregion` pragmas
- Do not alter anything in the [src/bench_precompiles](./src/bench_precompiles/) and [src/tests](./src/tests/) directories

## Consensus-critical code review

Changes to EVM execution, transaction processing, block validation, state transitions, or any code that affects what the chain accepts as valid require extra scrutiny. Nethermind is an Ethereum execution client — a subtle behavioral difference from the spec can cause a consensus split.

- **Observable equivalence**: when refactoring or optimizing consensus code, trace every code path (success, failure, out-of-gas, revert) and confirm the observable outcome (state changes, gas, return data, receipts) is identical to the original.
- **Fork-awareness**: Ethereum behavior changes across hard forks. Verify that fork-gated logic (`IReleaseSpec` checks) is evaluated correctly and that optimized paths do not bypass fork-specific rules. Remember that historical sync must replay all past forks.
- **Historical edge cases**: some forks required client-specific compatibility workarounds (e.g., the EIP-161 Parity touch bug for RIPEMD-160). These must not be broken by new code paths. When excluding code from an optimization, document _why_ so future contributors do not accidentally revert the exclusion.
- **Gas correctness**: gas charges, refunds, stipends, and the 63/64 forwarding rule must be preserved exactly. Arithmetic on gas values must guard against overflow.
- **State rollback**: snapshot/restore semantics must match — if the original code takes a snapshot and reverts on failure, any alternative path must produce the same net state.
- **Safe defaults**: new optimizations or alternative code paths should default to the conservative (original) behavior. Opt-in flags should be `false` by default so that untested paths are never activated implicitly.
- **Reference the spec**: when in doubt, refer to the Yellow Paper, the relevant EIP, or the [execution-specs](https://github.com/ethereum/execution-specs). Add a comment citing the EIP number when implementing fork-specific logic.

## Project structure

The codebase in [src/Nethermind](./src/Nethermind/) is organized into three independent solutions:

- [Nethermind.slnx](./src/Nethermind/Nethermind.slnx): The Nethermind client codebase and tests
- [EthereumTests.slnx](./src/Nethermind/EthereumTests.slnx): The Ethereum Foundation test suite
- [Benchmarks.slnx](./src/Nethermind/Benchmarks.slnx): Performance benchmarking

### Architecture

- **Entry point and initialization**
  - [Nethermind.Runner](./src/Nethermind/Nethermind.Runner/): The app entry point and startup orchestration
  - [Nethermind.Init](./src/Nethermind/Nethermind.Init/): Initialization logic, memory management, metrics
- **General API**
  - [Nethermind.Api](./src/Nethermind/Nethermind.Api/): Core API interfaces and plugin API
  - [Nethermind.Config](./src/Nethermind/Nethermind.Config/): Configuration handling
  - [Nethermind.Logging](./src/Nethermind/Nethermind.Logging/): Logging
- **Consensus algorithms**
  - [Nethermind.Consensus.AuRa](./src/Nethermind/Nethermind.Consensus.AuRa/): Authority round (Aura)
  - [Nethermind.Consensus.Clique](./src/Nethermind/Nethermind.Consensus.Clique/): Proof of Authority (PoA)
  - [Nethermind.Consensus.Ethash](./src/Nethermind/Nethermind.Consensus.Ethash/): Proof of Work (PoW)
  - [Nethermind.Merge.Plugin](./src/Nethermind/Nethermind.Merge.Plugin/): Proof of Stake (PoS)
- **Core blockchain**
  - [Nethermind.Blockchain](./src/Nethermind/Nethermind.Blockchain/): Block processing, chain management, validators
  - [Nethermind.Core](./src/Nethermind/Nethermind.Core/): Foundational types
  - [Nethermind.Crypto](./src/Nethermind/Nethermind.Crypto/): Core cryptographic algorithms
  - [Nethermind.Evm](./src/Nethermind/Nethermind.Evm/): EVM implementation
  - [Nethermind.Evm.Precompiles](./src/Nethermind/Nethermind.Evm.Precompiles/): EVM precompiled contracts
  - [Nethermind.Specs](./src/Nethermind/Nethermind.Specs/): Network specifications and hard fork rules
- **State and storage:**
  - [Nethermind.Db](./src/Nethermind/Nethermind.Db/): Database abstraction layer
  - [Nethermind.Db.Rocks](./src/Nethermind/Nethermind.Db.Rocks/): RocksDB implementation (primary storage backend)
  - [Nethermind.State](./src/Nethermind/Nethermind.State/): World state management, accounts, contract storage
  - [Nethermind.Trie](./src/Nethermind/Nethermind.Trie/): Merkle Patricia trie implementation
- **Networking:**
  - [Nethermind.Network](./src/Nethermind/Nethermind.Network/): devp2p protocol implementation
  - [Nethermind.Network.Discovery](./src/Nethermind/Nethermind.Network.Discovery/): Peer discovery
  - [Nethermind.Network.Dns](./src/Nethermind/Nethermind.Network.Dns/): DNS-based node discovery
  - [Nethermind.Network.Enr](./src/Nethermind/Nethermind.Network.Enr/): Ethereum Node Records (ENR) handling
  - [Nethermind.Synchronization](./src/Nethermind/Nethermind.Synchronization/): Block synchronization strategies (fast sync, snap sync)
  - [Nethermind.UPnP.Plugin](./src/Nethermind/Nethermind.UPnP.Plugin/): UPnP support
- **Transaction management:**
  - [Nethermind.TxPool](./src/Nethermind/Nethermind.TxPool/): Transaction pool (mempool) management, validation, sorting
- **RPC and external interface:**
  - [Nethermind.Facade](./src/Nethermind/Nethermind.Facade/): High-level API facades for external interaction
  - [Nethermind.JsonRpc](./src/Nethermind/Nethermind.JsonRpc/): JSON-RPC server
  - [Nethermind.Sockets](./src/Nethermind/Nethermind.Sockets/): WebSocket server
- **Monitoring**
  - [Nethermind.HealthChecks](./src/Nethermind/Nethermind.HealthChecks/): Health checks
  - [Nethermind.Monitoring](./src/Nethermind/Nethermind.Monitoring/): Monitoring API
  - [Nethermind.Seq](./src/Nethermind/Nethermind.Seq/): Seq integration
- **Serialization:**
  - [Nethermind.Serialization.Json](./src/Nethermind/Nethermind.Serialization.Json/): JSON serialization
  - [Nethermind.Serialization.Rlp](./src/Nethermind/Nethermind.Serialization.Rlp/): RLP serialization
  - [Nethermind.Serialization.Ssz](./src/Nethermind/Nethermind.Serialization.Ssz/): SSZ serialization
- **Third-party integration:**
  - [Nethermind.Flashbots](./src/Nethermind/Nethermind.Flashbots/): Flashbots integration
  - [Nethermind.Optimism](./src/Nethermind/Nethermind.Optimism/): Optimism network (OP Stack) support
  - [Nethermind.Taiko](./src/Nethermind/Nethermind.Taiko/): Taiko network support
- **Tests**
  - Test suites reside in Nethermind.\*.Test directories

## Pull request guidelines

Before creating a pull request:

- Ensure the code compiles
- Add tests covering your changes and ensure they pass:
  ```bash
  dotnet test --project path/to/.csproj -c release -- --filter FullyQualifiedName~TestName
  ```
- Ensure the code is well-formatted:
  ```bash
  dotnet format whitespace src/Nethermind/ --folder
  ```
- Follow the [pull_request_template.md](.github/pull_request_template.md) format: fill in the changes section, tick the appropriate type-of-change checkboxes, and complete the testing/documentation sections. The checkboxes drive automatic PR labeling.

## Prerequisites

See [global.json](./global.json) for the required .NET SDK version.

## Reproducible Benchmark Workflow Guidance

This repository contains a dedicated workflow for reproducible payload benchmarks:
- Workflow file: [`.github/workflows/run-expb-reproducible-benchmarks.yml`](./.github/workflows/run-expb-reproducible-benchmarks.yml)
- Main execution runner label: `reproducible-benchmarks`

### What the workflow does

- Resolves runtime inputs (branch, state layout, payload set, delay, optional extra flags).
- Selects one benchmark config file from `/mnt/sda/expb-data`.
- Builds or reuses Nethermind Docker image tag depending on branch rules.
- Renders a temporary config (does not modify source files) by:
  - replacing `<<DOCKER_TAG>>`
  - replacing `<<DELAY>>`
  - renaming scenario key `nethermind:` to a detailed scenario name
  - appending user-provided extra flags under `extra_flags:`
- Installs `expb` via `uv tool install --force --from ... expb`.
- Runs `expb execute-scenarios` with per-payload metrics and logs.
- Handles termination gracefully with cleanup grace period.
- On successful `master` push runs, caches per-payload timing aggregates extracted from the `processing_ms` table.
- On labeled PR runs, restores latest cached `master` metrics and posts a PR comment with PR vs master comparison.

### What to inspect in run output

- Inspect the `Run expb scenarios` step output first.
- Treat any Nethermind `Exception` as a high-priority issue.
- Explicitly scan logs for invalid block signals, including `Invalid Block` and `Invalid Blocks`.
- Review the end-of-run summary section with per-block timings and totals.
- Use summary timing values to derive aggregate metrics (average/mean at minimum; median/p95 when available).
- If a run fails or is terminated, check whether cleanup grace-period handling completed cleanly.

### Log structure reference

- Reference run used for structure validation:
  - Run: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008`
  - Job: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008/job/64159725161`
- Fetch logs with:
  ```bash
  gh run view 22185801008 --job 64159725161 --log
  ```
- GitHub job log lines are tab-separated in this shape:
  - `<job-name>\t<step-name>\t<timestamp>\t<message>`
  - Example step names in this workflow: `Print resolved inputs`, `Render benchmark config`, `Install or upgrade expb`, `Run expb scenarios`.
- `Run expb scenarios` contains mixed streams:
  - EXPB structured events like: `timestamp=... level=info event="..."`.
  - K6 progress and metric blocks (`http_req_duration`, `iteration_duration`, percentiles like `p(95)`).
  - Raw Nethermind runtime logs (received blocks, processed block timings, shutdown sequence).
  - Per-payload metrics table near the end, marked by:
    - `+---------+------------+-----------------+`
    - `| payload | gas_used   | processing_ms   |`
    - rows with payload id, gas used, processing time.
- ANSI color codes are present; when searching/parsing, strip ANSI escape sequences first.
- Some non-ASCII time-unit glyphs can appear mangled in plain terminal output, so prefer numeric metric fields when computing aggregates.

### Mandatory log checks

- Fail review if any of these appear in Nethermind logs:
  - `Exception`
  - `Invalid Block`
  - `Invalid Blocks`
- Workflow behavior requirement: any detected `Exception` in run output must fail the workflow after reporting matching lines.
- Also flag severe runtime signals if present:
  - `Unhandled`
  - `Fatal`
  - `ERROR`
- Confirm normal shutdown markers at end:
  - `Nethermind is shut down`
  - `event="Cleanup completed"`

### Notes for agents

- The benchmark config is rendered to a temporary file and removed afterward; no source config revert is required.
- For `pull_request` and `push` auto-runs, default mode is currently `halfpath + superblocks`.
- Keep benchmark-related changes isolated to the workflow and benchmark guidance unless explicitly asked otherwise.
