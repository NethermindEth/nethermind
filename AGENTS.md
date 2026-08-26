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
- When multiple solutions are viable, prefer them in this order: one that removes code, then one that adds code without adding surface area (new interfaces or public methods) or touching existing code, and last, one that modifies existing code. Removing code removes failure points; additive changes generally don't regress existing behavior and are the easiest to review. This ranks viable designs — a bug in existing code should still be fixed in place, not wrapped. If a change makes existing code unused, remove it.
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
- Execution runner: chosen by the `arch` input — `amd64` (default) runs on `reproducible-benchmarks`
  with snapshots under `/mnt/sda`; `arm64` runs on `reproducible-benchmarks-arm` with snapshots under
  `/data`. The ARM box carries a single snapshot set — Nethermind in the **flat** layout — so it
  refuses any other client, layout, or an image it would have to build; the amd64 box takes all of
  them. **Never compare timings across the two boxes.**

### What the workflow does

- Resolves runtime inputs (branch, state layout, payload set, delay, optional extra flags).
- Selects one benchmark config file from the runner's expb data dir (`/mnt/sda/expb-data` on amd64, `/data/expb-data` on arm64).
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
- The `dottrace` input selects a profiling mode — `false` (default), `sampling`, `tracing`, or `timeline` (`true` is a legacy alias for `sampling`) — and passes `--dottrace --dottrace-mode <mode>` to expb. Pick by question: `sampling` for "where does time go" (low overhead, the default choice), `tracing` for exact **call counts** (~4x overhead, so read its counts and distrust its times), `timeline` for waits/locks/GC over time. dotTrace snapshots (`.dtp` + chunk files; `.dtt` for timeline) are zipped and uploaded as artifacts.
- A downstream Windows job (`generate-dottrace-reports`) runs Reporter.exe to produce XML reports (`*-report.xml`) uploaded as the `dottrace-reports` artifact. Each report contains `<Function>` nodes with `FQN`, `TotalTime`, `OwnTime`, `Calls`, and full call stacks — sort by `OwnTime` for hot spots, use `CallStack` attributes for call tree analysis. **`timeline` produces no XML** (Reporter.exe cannot convert it) — that job is gated off, so analyze the snapshot in the dotTrace UI instead.
- Every profiled **EXPB** run also collects a **dotnet-trace EventPipe sidecar** (`.nettrace`, in the same `dottrace-*` artifact, or `profiling-*` when perf is enabled; the rpc-bench workflow does not collect one). It carries GC pause durations, lock contention, and exception events, which no CPU profile shows — use it whenever the question is about tail latency or stalls rather than hot code, and note it is the only structured output for `timeline` runs.
- Linux perf profile: pass `perf=true` to sample the client with `perf` on the host. This is the only way to see inside dotTrace's `[Native or optimized code]` node, which is routinely the third-largest entry in a snapshot: perf resolves managed frames from the runtime's perf map and native frames from the container's shared objects, so RocksDB, the allocator, `memset`/`memcpy` and GC time are attributed individually. The artifact carries `perf.folded` (one line per unique stack); raw `perf.data` is excluded. `perf` and `dottrace` are independent inputs; enabling both samples the process twice, so use perf runs for attribution and keep A/B timing numbers to dottrace-only or unprofiled runs.
  - Symbolization is partial and worth checking first: on a verified run 19% of samples resolved to
    managed frames, 55% to native ones (snappy, secp256k1, LZ4, kernel) and 26% stayed `[unknown]`,
    almost all of it inside the stripped `libcoreclr.so` and `librocksdb.so` shipped in the image.
    perf therefore narrows dotTrace's single opaque node to a named library plus a resolved majority,
    but it does not eliminate it - an unstripped build would be needed for the rest.
  - The capture covers every thread of the client process, RocksDB's background compaction pool included - on a short run that pool was 38% of process CPU and nearly half of it was snappy. Split by the leading `comm` field before attributing anything to block processing: `awk -F';' '$1==".NET"'` keeps the runtime's threads, `$1=="rocksdb:low"` the compaction ones.
  - perf samples CPU cycles, so idle threads are absent and the percentages are shares of CPU, not of wall clock. They are not comparable with dotTrace's wall-clock percentages.
- Targeted per-block dotTrace: pass `trace_blocks=<n1,n2,...>` (implies `dottrace=true`); the client's BlockProfiler plugin brackets each listed block. The artifact is one `.dtp` workspace with **one snapshot per traced block** (open in the dotTrace UI; `.dtp.NNNN` files are storage segments, not per-block files). The XML report merges all traced windows, so trace a single block per run when isolated XML matters.

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
- perf profiles are folded stacks, one line per unique stack. Use [`scripts/perf-report.sh`](./scripts/perf-report.sh): `top <perf.folded> [N]` for self time, `total` for inclusive time, `native` to list only unmanaged frames, and `compare <a.folded> <b.folded> [N]` for shifts between two profiles. Counts are reported as a share of the profile so runs of different length stay comparable. Pure awk, seconds even on large profiles.
- dotTrace XML reports are 50-70MB. **Never load full XML into context.** Use [`scripts/dottrace-report.sh`](./scripts/dottrace-report.sh): `top <report.xml> [N]` for hot spots, `compare <a.xml> <b.xml> [N]` for regressions/improvements. Runs in <2 seconds via grep+awk.

## RPC Benchmark Workflow Guidance

- Workflow file: [`.github/workflows/run-rpc-benchmarks.yml`](./.github/workflows/run-rpc-benchmarks.yml)
- Scripts and full reference: [`scripts/rpc-bench/README.md`](./scripts/rpc-bench/README.md)
- [Linux perf flow](./scripts/rpc-bench/README.md#linux-perf-flow) documents the root-only RPC capture contract; [`scripts/perf-report.sh`](./scripts/perf-report.sh) reads folded profiles from both EXPB and rpc-bench.

`run-rpc-benchmarks` measures state-reading JSON-RPC (`eth_call`, `eth_getBalance`, `trace_*`,
`debug_*`) against a parked DB snapshot on the same two benchmark runners as expb — pick the box with
`arch`, and always pass `docker_image` explicitly so the runner pulls a prebuilt tag rather than
building one. For an A/B use `benchmark_tool=jsonbench-sweep` with `tool_config.clients` listing one
`nethermind@<image>` per arm (the first is the response-parity baseline, compared byte-for-byte), then
dispatch the same config a second time with the arms swapped, because position artifacts on this rig
reach ~10% and have pointed in opposite directions on different workloads.

### What the runners actually hold

Both boxes carry **one** private `eth_call` corpus, `eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz`
= **497 records** (heavy simulation traffic: every record carries state overrides, median ~331 KiB). The
sweep discovers it by glob and prints `Corpus scenarios: …` / `corpus OK: 497 records` — read those lines
rather than assuming a corpus set. Pin one with `corpus_glob` when more are added.

The canonical cell is **100 rps for 120 s after a discarded 60 s warm-up at 400 rps**. Rates are
the thing to get right:

| rate | usable? |
|---|---|
| 10 | **no** — 300 requests gives mean CV ~70%, p99 CV ~206%; one cold outlier dominates |
| 50–100 | yes; CV ~1–3% on mean/p50, p99 needs n>=3 |
| 300 | amd64 only — on arm64 it drove a **1.22% HTTP fail rate**, tripping the 1% gate, after which percentiles above p98 describe failures, not latency |

Size a cell by request count instead of duration with `corpus_requests` (absolute) or `corpus_passes`
(a multiple of the corpus's record count) — `corpus_passes: 5` on 497 records at 100 rps is ~2,485
requests. Note these are draws *with replacement*, so coverage is `N x (1 - (1 - 1/N)^requests)`, not a
full pass. `corpus_parity.py` refuses corpora above **10,000 records** unless `max_corpus_records` is
raised, and the k6 fixture is the real ceiling long before parity is (~142 MB for 497 records), so a
50k-record capture wants sampling down rather than a bigger cap.

For reference, expb's sweeps on the same boxes are sized by `amount`: `superblocks` defaults to 100,
`realblocks` and `fusaka` to 1000, and both of the latter have 10k payloads available (fusaka covers
blocks 25,490,001-25,499,999). Separately, `benchmark_tool=ethcallchaos` uses the EthCallChaos SQLite
corpus (`corpus-v2`, ~1.1 GB) rather than these JSONL corpora, and with a seeded corpus it re-reports
its own stale timings — use the json-bench per-category config for an A/B instead.

```bash
gh workflow run run-rpc-benchmarks.yml --ref <branch> \
  -f arch=amd64 -f benchmark_tool=jsonbench-sweep \
  -f docker_image=nethermindeth/nethermind:master-<sha> \
  -f tool_config='{"clients":"nethermind@nethermindeth/nethermind:master-<sha> nethermind@nethermindeth/nethermind:<pr-tag>","rps_list":"100","duration":"120s"}'
```
