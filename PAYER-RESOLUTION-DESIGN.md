# EIP-8141 — Payer resolution at mempool admission (design)

Internal design pass, 2026-07-29. Branch `eip8141-payer-resolution` (base
`eip8141-frame-txs-devnet7`). Verified against the live EIP
(`svlachakis/EIPs@eip-8141-canonical-paymaster`) Mempool section, and against the frame-tx
execution loop in `TransactionProcessorBase.FrameTx.cs`.

## Problem

In a frame transaction the fee-payer is chosen by EVM code *inside* the transaction — an
`APPROVE(APPROVE_PAYMENT | APPROVE_EXECUTION_AND_PAYMENT)` issued from some account during a
`VERIFY` frame. Unlike every other transaction type, the pool cannot know who pays from the
transaction fields alone. Today `Nethermind.TxPool` never resolves this: there is no payer on
the pool-side `Transaction`, and frame txs are admitted only under the active fork by
`NotSupportedTxFilter` (`EIP8141-GAP TEMPORARY`). Every downstream mempool-policy piece —
per-payer exposure accounting, revalidation on payer balance/code change, replacement
payer-switch, invalidated-first eviction, public-mempool DoS rules — is blocked on first being
able to resolve the payer at admission. This layer unblocks them.

## Key decision: native resolution of legible prefixes, no EVM simulation in the pool

The foundational layer resolves the payer **natively** — by reading account state
(`code hash`, `nonce`, `balance`) that the pool already holds via
`IChainHeadInfoProvider.ReadOnlyStateProvider` — for the *legible* validation prefixes, i.e. the
prefixes whose payer is decidable from protocol-defined semantics with no deployed code to run.
Non-legible prefixes (a `VERIFY` frame against a deployed smart-account, a non-canonical
paymaster, a deploy-factory frame) are reported as `RequiresSimulation` and left for a later PR.

Rationale — why native, not full simulation:

- **The pool has a state reader, not a processor.** `Nethermind.TxPool` holds only
  `IReadOnlyStateProvider`. Full validation-prefix simulation needs an `IWorldState` +
  `ITransactionProcessor`, which are **scoped, per-block, and not thread-safe** (di-patterns.md).
  The pool admits transactions concurrently, so wiring a processor in would mean a per-admission
  world-state scope with its own lifetime/concurrency management — a large, risky change, and
  over-building for a foundational slice.
- **The mass-market flows are all legible.** The `fast-admission-design` handoff establishes
  that EOA self-pay, sponsored-EOA (default-code sponsor), and canonical-paymaster flows resolve
  natively in ≤2 state reads. The recognized public-mempool prefixes (`self_verify`,
  `only_verify | pay`, with optional `deploy` / `expiry_verify`) are exactly these shapes.
- **Correctness is preserved.** The native evaluation mirrors the execution loop's
  `ExecuteDefaultCode` / `ApplyApproval` structural conditions, so for a legible prefix the
  resolved payer equals the payer execution would set. Anything not provably equal falls to
  `RequiresSimulation` rather than guessing.

This is the same architecture the spec blesses in *Direct Evaluation of Protocol-Defined Frames*
(γρ.789): "direct evaluation of the protocol-defined semantics is equivalent to simulation, and
a node MAY use it." The foundational slice implements direct evaluation; general simulation is
the documented follow-up.

## Where it hooks in

- **Component:** `FrameTxPayerResolver` (new, `Nethermind.TxPool`) — a pure function
  `Resolve(Transaction, IReadOnlyStateProvider) → FrameTxPayerResolution`. Stateless, no EVM,
  independently unit-testable.
- **Wiring:** a thin `FrameTxPayerFilter : IIncomingTxFilter` added to the `TxPool` post-hash
  filter list (constructed in-line like the other filters — no new DI registration, matching the
  existing pattern). It runs only on `tx.SupportsFrames`, calls the resolver against the
  chain-head read-only state, stores the resolved payer on the transaction, and **always returns
  `Accepted`**. It is an *annotation* step, not a gate: rejection belongs to the deferred DoS
  policy. Placed last in the post-hash pipeline so only otherwise-admissible frame txs are
  resolved (fork-active gating already happened pre-hash in `NotSupportedTxFilter`; sender is
  already recovered by `MalformedTxFilter`).

## What it produces

`FrameTxPayerResolution` (readonly struct):

- `Outcome ∈ { Resolved, NoPayer, RequiresSimulation }`
  - `Resolved` — a legible prefix that sets a payer; `Payer` is that address.
  - `NoPayer` — a legible prefix that provably never sets a payer (execution would fail the
    terminal payer gate → invalid tx). `Payer` is null.
  - `RequiresSimulation` — the prefix reaches deployed code the pool cannot evaluate natively;
    deferred to the simulation PR. `Payer` is null.
- `Payer` (`Address?`) — stored on the pool-side `Transaction.PayerAddress` (new in-memory-only
  property, mirroring how `SenderAddress` is pool-populated and how the receipt already carries
  `payer`).
- `DependencySet` (`FrameTxDependencySet`) — the state this resolution depends on, per γρ.795:
  sender code hash + nonce; payer code hash + balance; and, when an `expiry_verify` frame is
  present, the `EXPIRY_VERIFIER` code hash, the frame's deadline, and a `DependsOnTimestamp`
  flag. Captured and returned (and tested); **indexing** it for O(1) revalidation is deferred.

### Legible recognition (mirrors the spec's Recognized Validation Prefixes)

After skipping an optional leading `expiry_verify` frame (VERIFY, target `EXPIRY_VERIFIER`,
flags 0, 8-byte data — structurally validated already; it does not set the payer, but adds the
expiry + timestamp dependency):

| Prefix | Legible when | Resolved payer |
|---|---|---|
| `self_verify` (VERIFY, flags `0x3`, target null/sender) | sender has **default code** (empty code hash) and sig[0] is secp256k1 / empty-msg / resolves to sender | `sender` |
| `only_verify` (VERIFY, flags `0x2`, target null/sender) then `pay` (VERIFY, flags `0x1`) | sender is default-code (sig[0] as above) **and** the `pay` target has default code with sig[1] secp256k1 / empty-msg / resolving to the target (a default-code EOA sponsor) | `pay` target |

Everything else → `RequiresSimulation`, except a legible prefix that terminates without any
payment approval → `NoPayer`. A structural signature-shape failure in a legible frame → `NoPayer`
(execution would revert that `VERIFY` frame and invalidate the tx). No cryptographic recovery is
performed here — that, and the `MAX_VERIFY_GAS` budget, are part of the deferred DoS layer.

Deferred to simulation (all → `RequiresSimulation` today):
- `deploy | self_verify` and `deploy | only_verify | pay` — the deploy frame runs arbitrary
  factory code.
- `self_verify` / `only_verify` against a **deployed or EIP-7702-delegated** sender.
- `pay` against a **canonical paymaster** — recognition is by runtime-code-hash match, and the
  canonical paymaster code hash is *not yet pinned* by the spec ("will be added to this section")
  nor present in `Eip8141Constants`. This is a genuine blocker (see Open Questions).
- `pay` against a **non-canonical deployed paymaster**.

## Cost / DoS model (context; enforcement deferred)

The native path costs at most two structural signature-shape checks and ≤2 account reads per
frame tx — bounded and not itself a DoS vector, matching the `fast-admission-design` analysis.
The enforceable DoS bounds are **not** part of this slice and remain follow-ups:

- `MAX_VERIFY_GAS` (100_000) over signature-verification cost + evaluated frame work.
- Per-scheme signature *cryptographic* verification (secp256k1 2800 / P256 6700 / ARBITRARY 100),
  incl. future PQ schemes — verified before frames run; the payer path itself does no recovery.
- Canonical-paymaster balance reservation and non-canonical `MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER`
  cap.
- Per-payer exposure accounting (the #12007 generalization: summed pending max costs ≤ payer
  balance) — the primary consumer of the resolved payer this layer produces.

The resolver deliberately does **not** enforce payer solvency: it resolves *who* the structural
payer is; whether that payer is solvent is the exposure/reservation gate (deferred). Balance is
captured in the dependency set so the deferred gate has it.

## Revalidation on head change

The resolution is a pure function of `(transaction fields, head account state)`, so on a head
change re-resolution is idempotent and cheap: re-run `Resolve` for the affected frame txs. The
foundational slice does **not** add a revalidation pass; it coexists with:

- the sibling **#12603** (`eip8141-payer-exposure`) expiry-eviction pass already on the
  head-change path — no overlap (that evicts on deadline; this only annotates).
- the deferred dependency-set **indexing** (#12001/#12007 model) that will let revalidation touch
  only dependency-affected txs instead of re-resolving all. Until then, re-resolution is correct,
  just not yet optimized.

## Data model / storage

- `Transaction.PayerAddress` — nullable, in-memory-only (not RLP-encoded, cleared on
  `ClearPreHash`/copy like other pool-side fields), the resolved payer.
- `FrameTxPayerResolution` / `FrameTxDependencySet` — value types returned by the resolver.
  Storing/indexing the dependency set in a pool-side structure keyed by dependency (for
  revalidation) is **deferred**; re-resolution stands in until then.

## Staging

**Foundational slice (this PR):**
1. `FrameTxPayerResolver` + `FrameTxPayerResolution` + `FrameTxDependencySet` + `FrameTxPayerOutcome`.
2. Native resolution of the legible default-code `self_verify` and `only_verify | pay` prefixes,
   with the optional leading `expiry_verify` frame handled, dependency set captured.
3. `FrameTxPayerFilter` wiring the resolver into the post-hash pipeline; `Transaction.PayerAddress`.
4. Unit tests: self-paid, sponsored (default-code sponsor), default-code shapes, never-sets-payer
   → none, requires-simulation cases, dependency-set capture.

**Deferred (documented `EIP8141:` follow-ups referencing this doc + ethereum/EIPs#12007):**
- General validation-prefix **simulation** for non-legible prefixes (deployed smart-account
  senders, deploy-factory frames, non-canonical paymasters).
- **Canonical paymaster** recognition once the runtime-code hash is pinned upstream.
- **Per-payer exposure accounting** and canonical/non-canonical paymaster **reservation**
  (#12007) — keyed off `Transaction.PayerAddress`.
- **Revalidation** pass that re-resolves only dependency-touched txs, and **dependency-set
  indexing**.
- **Replacement** payer-switch (atomic release/take of the old/new payer exposure).
- **Eviction** ordering (invalidated-first, nearest-deadline, lowest-priority-fee).
- **Cryptographic** signature verification + `MAX_VERIFY_GAS` enforcement at admission.
- Tightening the `NotSupportedTxFilter` `TEMPORARY` gate to require the DoS filters above.

## Open questions / genuine forks for Marc

1. **Canonical paymaster code hash is unpinned.** The spec keys canonical-paymaster recognition
   on a runtime-code hash that "will be added to this section"; it is absent both upstream and in
   `Eip8141Constants`. Until it lands, *every* paymaster `pay` frame is `RequiresSimulation`, so
   the sponsored-via-canonical-paymaster mass-market flow is not natively resolvable yet. This is
   an upstream blocker, not a Nethermind gap — flag for coordination. (The default-code EOA
   sponsor flow *is* resolvable and is covered.)
2. **`Transaction.PayerAddress` on Core vs. pool-side side-table.** I add a nullable in-memory
   property on `Transaction` (matches the `SenderAddress` precedent and the receipt's `payer`).
   The alternative — a `ConditionalWeakTable`/dictionary inside the pool — keeps Core clean but
   is clumsier for RPC/receipt reuse. Chosen: the property. Reversible if we prefer to keep the
   pool concept out of Core.
3. **`NoPayer` vs. `RequiresSimulation` as a future reject signal.** This slice never rejects. A
   later DoS PR must decide whether `NoPayer` is an immediate reject (provably invalid) while
   `RequiresSimulation` triggers the simulation path. Pinning the enum now makes that a
   one-line policy change later.
4. **Default-code sponsor classification (from #12007 H3).** Whether a default-code (codeless
   EOA) sponsor is a "non-canonical paymaster" (cap = 1) or governed by per-payer exposure is an
   open spec ambiguity. This slice is agnostic — it only resolves the payer address; the
   classification lives in the deferred exposure layer.
