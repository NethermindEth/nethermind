# AGENTS instructions

This guide helps to get started with the Nethermind Ethereum execution client repository, which targets `net10.0` and uses C# language version `14.0`. It covers the project structure, how to build and test the code, and follow the PR workflow.

## Repo structure

- [src/Nethermind](./src/Nethermind/): The Nethermind codebase
- [tools](./tools/): Various servicing tools for testing, monitoring, etc.
- [scripts](./scripts/): The build scripts and stuff used by GitHub Actions workflows
- See [README.md](./README.md) for more info

## Coding guidelines and style

- Follow [CONTRIBUTING.md](./CONTRIBUTING.md) and [.editorconfig](./.editorconfig)
- An agent's primary concern is correctness. Next after that is reviewer fatigue.
- Keep changes minimal and focused — don't touch unrelated code. Try to minimise the diff from the base branch, for example, not reordering code or making stylistic changes unless they improve code clarity.
- On unrelated code, be even more conservative: do not rephrase comments, and do not even fix typos. That is the responsibility of a linter. Keep the code unchanged verbatim.
- When designing a solution, try to design as a plugin, altering behavior through module registration without modifying existing code (see [di-patterns.md](./.agents/rules/di-patterns.md)). Even if not a plugin, it's generally a good idea to alter behavior without changing current code:
  - Where possible, do not add additional interfaces or public methods — this tends to break plugins, cause unnecessarily tight coupling, and make implications harder to reason about.
  - Prefer composition over inheritance — inheritance has caused many extensibility issues in this code base.
- Prefer solutions in this order: first removing code, then adding code, and only when neither works, modifying existing code. If a change makes existing code unused, remove it.
- When fixing a bug, always add a regression test
- Do not alter [src/bench_precompiles](./src/bench_precompiles/) or [src/tests](./src/tests/)
- Prefer self-documenting code — clear names and structure should remove the need for most comments. Emit a comment only when it captures context that is not obvious from the code itself: the _why_ behind a non-obvious choice, an invariant, a workaround, an EIP/Yellow-Paper reference, a subtle edge case, etc. Comments that merely restate the code are noise — don't add them, and remove them when you encounter them. Keep comments concise and ensure that they make sense in the context of the master branch, not referencing the specifics of the current session.
- When in doubt, do not add a comment. An unnecessary comment contributes to reviewer fatigue.
- For member-level documentation (methods, constructors, properties, types), prefer XML doc comments over in-line comments whenever the explanation applies to the member as a whole:
  - `<summary>` — one or two sentences describing _what_ the member does from the caller's perspective: its contract, purpose, and what it returns/represents. Keep it short enough to be useful in IntelliSense; do not describe implementation details or rationale here.
  - `<remarks>` — the longer-form explanation that does not belong in the summary. Use it for any of: algorithmic approach, design rationale, pre/postconditions and invariants, thread-safety guarantees, performance characteristics, side effects, edge cases, EIP / Yellow-Paper / spec references, and notable caveats for callers.
  - Use `<param>`, `<returns>`, `<exception>`, and `<typeparam>` for parameter/return/exception/type-parameter specifics rather than stuffing them into `<summary>` or `<remarks>`.
  - For interface implementations and overrides, prefer `<inheritdoc/>` (optionally with `cref=`) to propagate the contract from the base/interface instead of duplicating it. Add `<remarks>` only when the implementation introduces caller-visible behavior beyond the inherited contract.
  - Reserve in-line comments for implementation-specific details that cannot reasonably live on the member header — e.g. why a particular branch is taken, why a value is computed this way at this exact spot, or a local workaround for a bug elsewhere.
- Avoid code duplication, especially in tests:
  - When tests differ only by inputs and expected outputs, parameterize a single test with `[TestCase(...)]` or `[TestCaseSource(...)]` rather than copy-pasting the body. Before adding a new test, check whether an existing one can be extended with another `[TestCase]`.
  - When only _parts_ of tests are similar (shared setup, common assertions, recurring scenarios), factor those parts into helper methods or helper types (e.g. a builder, a shared static helper, a test fixture base). Keep each test body focused on what makes the case unique.
  - See [`.agents/rules/test-infrastructure.md`](./.agents/rules/test-infrastructure.md) "Test guidelines" for details.

---

## Codebase Rules

Detailed rules live in [`.agents/rules/`](./.agents/rules/). **You MUST read the relevant files before answering any query, reasoning, writing, reviewing, planning, or debugging any code read load additional files as soon as the task touches their domain. Do NOT skip loading a file because you think you already know the rules — always read from disk.**

- [coding-style.md](./.agents/rules/coding-style.md) — Almost always. Load for any task requiring C#-specific reasoning. Covers syntax, coding patterns, documentation, and code quality.
- [di-patterns.md](./.agents/rules/di-patterns.md) — Core dependency injection patterns. Load when working with DI registration, service wiring, or component architecture. Covers Autofac modules, WorldState architecture, lifetimes, and the custom DSL.
- [test-infrastructure.md](./.agents/rules/test-infrastructure.md) — Load when working with tests, benchmarks, or designing components that need to be testable. Covers TestBlockchain, benchmark setup, DI anti-patterns, and test guidelines.
- [robustness.md](./.agents/rules/robustness.md) — Almost always. Load for any task requiring C#-specific reasoning. Covers async pitfalls, resource management, thread safety, input validation, and unsafe blocks.
- [performance.md](./.agents/rules/performance.md) — Load when working on hot paths in the codebase. Covers ref structs, Span, SIMD, function pointers, and zero-allocation patterns.
- [package-management.md](./.agents/rules/package-management.md) — Load when working with NuGet dependencies. Covers Central Package Management (CPM) rules.
- [github-workflows.md](./.agents/rules/github-workflows.md) — Load when working with GitHub Actions, CODEOWNERS, or PR templates. Covers workflow conventions and automation patterns.
- [git.md](./.agents/rules/git.md) — Load when interacting with git version control. Covers merging, rebasing, pushing, and more.
- [agent-skills.md](./.agents/rules/agent-skills.md) — Load when working with agentic skills. Covers the symlink convention.

---

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
- Metrics source: prefers SSE client metrics (`[payload-server] client_metric` lines — Nethermind internal processing times) over K6 TTFB. Falls back to the per-payload pipe table when SSE data is unavailable.
- On successful `master` push runs, caches timing aggregates (AVG/MEDIAN/P90-P99/MIN/MAX). On PR runs, posts a comparison comment.
- The `single-summary` job aggregates across runs and payload sets into `GITHUB_STEP_SUMMARY` (per-run table + mean/best/worst when `run_count > 1`).
- When `dottrace` input is enabled, passes `--dottrace` to expb. dotTrace snapshots (`.dtp` + chunk files) are zipped and uploaded as artifacts. A downstream Windows job (`generate-dottrace-reports`) runs Reporter.exe to produce XML reports (`*-report.xml`) uploaded as the `dottrace-reports` artifact. Each report contains `<Function>` nodes with `FQN`, `TotalTime`, `OwnTime`, `Calls`, and full call stacks — sort by `OwnTime` for hot spots, use `CallStack` attributes for call tree analysis.

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
- For `pull_request` and `push` auto-runs, default mode is `flat` layout with both `superblocks` and `realblocks` payload sets.
- Keep benchmark-related changes isolated to the workflow and benchmark guidance unless explicitly asked otherwise.
- Optional low-variance mode: pass `-f expb_env="EXPB_EVM_WARMUP=1"` to enable expb's per-block EVM warmup (`eth_simulateV1` before each measured block). It serves the measured block's reads from warm caches, which lowers both run-to-run CV (~1.8%→~0.55% on flat-realblocks) and AVG. Pair it with a raised RPC gas cap — `-f additional_extra_flags="--JsonRpc.GasCap=1000000000000"` — otherwise the per-request gas budget (default 100M) is exhausted on dense blocks and the warmup `eth_simulateV1` calls fail with `-38013` (intrinsic gas), silently leaving those blocks un-warmed. Caveat: warmup minimizes cold RocksDB/storage interaction, so it is a low-variance *compute* signal, not a substitute for the default cold benchmark — don't use it when measuring storage-layer changes.
- dotTrace XML reports are 50-70MB. **Never load full XML into context.** Use [`scripts/dottrace-report.sh`](./scripts/dottrace-report.sh): `top <report.xml> [N]` for hot spots, `compare <a.xml> <b.xml> [N]` for regressions/improvements. Runs in <2 seconds via grep+awk.
