---
name: review
description: Deep code review for an Ethereum execution client. Checks consensus correctness, security, robustness, performance, breaking changes, and observability. Use when asked to "review", "check this PR", "look for bugs", "audit", or "review my changes".
allowed-tools: [Read, Grep, Glob]
---

# Code review

Nethermind is an Ethereum execution client built on .NET. Consensus correctness is non-negotiable — a wrong opcode, a missed fork condition, or an Engine API violation means invalid blocks on a live network.

**Only comment when you have HIGH CONFIDENCE (>80%) that a real issue exists.**
Be concise: one sentence per comment when possible. If uncertain, stay silent.

---

## Skip these — CI already handles it

Do not comment on formatting, build errors, test failures, CodeQL findings, dependency vulnerabilities, hive test results, or JSON-RPC output correctness. CI blocks the merge on all of these. Also skip: naming conventions, missing XML docs on `internal`/`private` members, minor grammar, refactoring suggestions that don't fix a real bug, logging improvements (unless security-related), build warnings with no behavioural impact.

---

## Test quality

CI runs all tests and fails on regressions — don't flag test failures. Do flag genuine gaps:

- A new consensus rule, EIP, or opcode change with no corresponding test — wrong EVM behaviour cannot be caught without one
- Tests that cover only the happy path and miss **boundary conditions** (e.g., block number exactly at a fork, gas exactly at the limit, `UInt256.MaxValue`)
- A bug fix with no regression test — without one, the bug will return
- Tests using `Thread.Sleep` or wall-clock time instead of deterministic mocks — these are flaky in CI
- Integration tests written for pure algorithmic logic with no I/O or state dependency — unit tests are faster and easier to debug; integration tests are appropriate when multiple components or real storage are genuinely involved
- Performance-sensitive code paths with no benchmark or allocation test — throughput regressions are invisible without measurements

---

## Documentation

Only flag when it genuinely affects correctness or usability:

- A new public interface (`IPlugin`, `INethermindPlugin`, JSON-RPC method, Engine API handler, config key) with no XML doc comment — public surfaces need documentation because consumers cannot read intent from implementation
- A comment or doc that **contradicts the code** — misleading docs are worse than no docs
- A non-obvious consensus rule or algorithm applied with no reference to the EIP or Yellow Paper section — link it so future reviewers can verify
- Config keys whose units or defaults are ambiguous (e.g., is it milliseconds or seconds?)

---

## Consensus & EVM correctness (CRITICAL)

Wrong EVM behaviour produces invalid blocks, causing chain desync and validators building on an invalid chain — leading to missed attestations and potential safety failures.

- EVM opcode with incorrect gas cost, wrong stack effect, or wrong memory expansion rule
- Fork/EIP activation condition checked by block number when it should use timestamp (all forks from Cancun onward activate by timestamp), or applied at the wrong boundary
- Engine API payload accepted or rejected contrary to the spec — each version has distinct required fields: `engine_newPayloadV1` (pre-Shanghai), `engine_newPayloadV2` (+ `withdrawals`), `engine_newPayloadV3` (+ `blobGasUsed`, `excessBlobGas`, `parentBeaconBlockRoot`), `engine_newPayloadV4` (+ `executionRequests`)
- Blob transaction (EIP-4844) handling with incorrect blob gas accounting, wrong blob base fee calculation, missing KZG commitment verification, or blob gas limit not enforced (6 blobs per block pre-Electra)
- Wrong state root computation — trie updates, storage root recomputation, or nonce/balance changes that don't match the block header's `stateRoot`; this is the ground truth for block validity
- Incorrect receipt fields (`cumulativeGasUsed`, bloom filter, logs, status) — receipts are hashed into `receiptsRoot` in the block header; wrong values propagate as invalid blocks to peers
- System-level withdrawal processing (EIP-4895) applied out of order, to the wrong address, or with wrong amounts — withdrawals bypass the gas system and any error corrupts the state root
- EIP-1559 base fee computed incorrectly from parent gas used vs target — wrong base fee breaks transaction validity and fee burning for every subsequent block
- Incorrect RLP encoding or decoding of any Ethereum data structure
- `UInt256` arithmetic that could overflow or truncate (Ethereum arithmetic is exact 256-bit)
- BLS12-381 or secp256k1 operations with wrong input format or missing validation
- EIP-2930 access list gas accounting applied incorrectly — warm vs cold storage access costs (EIP-2929) must account for pre-warmed slots declared in the access list
- Keccak-256 computed using `System.Security.Cryptography.SHA3_256` instead of Nethermind's `ValueKeccak` / `KeccakHash` — NIST SHA3 and Ethereum's Keccak-256 use different padding and produce different output on every input

### Key code locations

- EVM instruction handlers: `src/Nethermind/Nethermind.Evm/VirtualMachine.cs`
- Gas cost tables: `src/Nethermind/Nethermind.Evm/GasCostOf.cs`
- Engine API handlers: `src/Nethermind/Nethermind.Merge.Plugin/Handlers/`
- Fork activation specs: `src/Nethermind/Nethermind.Specs/`
- State root computation: `src/Nethermind/Nethermind.State/`
- Receipt processing: `src/Nethermind/Nethermind.Blockchain/Receipts/`

---

## Security

- Hardcoded JWT token, private key, credential, or secret in any form
- Engine API endpoints (`engine_*`) reachable without JWT authentication
- P2P or JSON-RPC input used in a context that allows command injection or path traversal
- `unsafe` block without a comment justifying the safety invariant — reviewers cannot verify correctness without it
- Exception message or log entry that includes a private key, seed, or user credential
- Missing validation on data received from untrusted sources (peers, RPC callers)
- P2P message handlers that process externally-supplied sizes or counts without an upper bound — Ethereum's devp2p has no built-in rate limiting, so unbounded `GetBlockHeaders` ranges or oversized transaction batches are a DoS vector

---

## C# / .NET robustness

- `async void` — exceptions are silently swallowed; use `async Task`
- `.Result` or `.Wait()` on a `Task` inside an `async` method — deadlock or thread-pool starvation risk; use `await` instead
- Missing `await` on an async call where fire-and-forget is not the documented intent
- Async method performing I/O or network calls without accepting a `CancellationToken` — uncooperative with graceful shutdown
- `IDisposable` / `IAsyncDisposable` object (especially `IDb`, streams, channels) not wrapped in `using` — resource leak
- Shared mutable state (caches, peer tables, chain state) modified from multiple threads without synchronisation
- `NullReferenceException` risk when processing untrusted external input without a null check or guard
- Exception silently swallowed in an empty `catch` block — lost diagnostics

---

## Performance

Nethermind is the fastest Ethereum execution client. Guard regressions in hot paths.

- Unnecessary heap allocation inside block processing or EVM execution loops (prefer `Span<T>`, `stackalloc`, `ArrayPool<T>`)
- `byte[]` passed through a hot call stack where `ReadOnlySpan<byte>` or `Memory<byte>` would avoid copies
- Blocking I/O call inside an `async` path — ties up thread-pool threads
- Database reads inside a tight loop without batching via `IBatch` or caching — amplifies I/O significantly
- LINQ with closures or `ToList()` in per-block or per-transaction logic — allocates on every call
- Large object allocations that could be pooled with `ObjectPool<T>` or `ArrayPool<T>`

---

## Observability

- A new code path that can fail silently with no log, metric, or counter — silent errors are the hardest to diagnose on a running node
- A new sync stage, block processor step, or P2P handler with no metrics — throughput regressions will go undetected

---

## Dependencies

- A new NuGet package added to a core or network project that is large, has a restrictive licence, or duplicates existing functionality — new dependencies increase binary size and attack surface

---

## API & breaking changes

These are the most expensive issues to fix after release and the hardest for automated tools to catch. Always review carefully.

### Breaking changes (flag any of these)

- **JSON-RPC method signature changed** — wallets, dApps, indexers, and monitoring tools depend on exact field names, types, and ordering; any removal or rename is a breaking change
- **Engine API response shape changed** — consensus clients (Lighthouse, Prysm, Teku, Nimbus, Lodestar) call these methods on every slot; a schema change breaks beacon node integration
- **Public plugin interface modified** (`IPlugin`, `INethermindPlugin`, or any interface in `Nethermind.Core`) without a versioning strategy — third-party plugin authors break silently at runtime
- **Config key renamed, removed, or type-changed** — existing node operators' config files will silently stop working or apply wrong values
- **Metric name or log format changed** — breaks dashboards, alerting rules, and log-parsing pipelines in production deployments

### API design

- A new public interface or class that exposes an implementation detail (e.g., a `RocksDb`-specific type in a `Core` namespace) — internals that leak into the contract become impossible to change later
- A new method added to an existing `IPlugin` interface without a default implementation — all existing plugin authors break at compile time
- Two ways of doing the same thing added without deprecating the old one — creates lasting ambiguity for callers
- A new JSON-RPC or Engine API parameter that is required but has no backwards-compatible default — breaks existing callers that don't send the new field
- Chain-specific logic added to `Nethermind.Core`, `Nethermind.Evm`, or `Nethermind.Blockchain` instead of the appropriate chain plugin (`Nethermind.Optimism`, `Nethermind.Merge.Plugin`, etc.) — poisons the shared abstraction

### File headers

New source files missing the required SPDX header (replace year with the current year):
```
// SPDX-FileCopyrightText: <year> Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```

---

## Response format

1. **Problem** — one sentence: what is wrong
2. **Impact** — one sentence: why it matters in an Ethereum client context *(omit if obvious)*
3. **Fix** — a concrete suggestion or short code snippet

**Example:**

> `GetGasCost` returns `long` but is compared against a `UInt256` balance — values above `long.MaxValue` will silently truncate and pass the check incorrectly.
> Cast before the comparison: `if ((UInt256)gasCost > balance)`.

---

## Real-world examples

### `async void` swallows exceptions

**File:** `Nethermind.Merge.Plugin/Synchronization/StartingSyncPivotUpdater.cs`

```csharp
private async void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs syncMode)
{
    if ((syncMode.Current & SyncMode.UpdatingPivot) != 0 && ...)
    {
        if (await TrySetFreshPivot(_cancellation.Token))
        ...
```

**Problem:** `async void` event handler — if `TrySetFreshPivot` throws, the exception is swallowed and the sync pivot silently stops updating.

### `.Result` inside a lock — deadlock

**File:** `Nethermind.Synchronization/FastBlocks/FastHeadersSyncFeed.cs`

```csharp
int requestSize =
    _syncPeerPool.EstimateRequestLimit(..., cancellationToken).Result
    ?? GethSyncLimits.MaxHeaderFetch;
```

**Problem:** `.Result` blocks the thread inside a lock. If the async continuation tries to reacquire the same lock, the node deadlocks and sync stalls permanently.

### `.Wait()` in Dispose — shutdown hang

**File:** `Nethermind.Trie/Pruning/TrieStore.cs`

```csharp
public void Dispose()
{
    _pruningTaskCancellationTokenSource.Cancel();
    _pruningTask.Wait();
```

**Problem:** `.Wait()` in `Dispose()` can deadlock during shutdown, requiring a kill signal.

### Wrong layer for state check (PR #10534)

Reviewer flagged binding `IPruningConfig` into `BlockchainBridge`:

> "Please don't bind IPruningConfig with BlockchainBridge. This will break flat as flat does not use this. Instead add the logic within TrieStore."

**Problem:** Pruning boundary check added to the RPC facade instead of the trie layer. Flat-layout nodes get an unnecessary dependency, and other state-reading code paths remain unprotected.

### Wrong constant for pruning boundary (PR #10534)

> "use `PruningConfig.PruningBoundary` instead `Reorganization.MaxDepth`"

**Problem:** Reorg depth and pruning boundary are independent values. Using the wrong one means nodes could reject valid state queries or serve partially pruned state, causing TrieExceptions.

---

## When to stay silent

If you are not at least 80% confident that something is a real problem, do not comment.
One high-confidence comment is worth more than five uncertain ones.
