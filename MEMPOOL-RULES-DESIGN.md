# EIP-8141 — Public-mempool rules (design)

Internal design pass, 2026-07-30. Branch `eip8141-mempool-rules` (base
`eip8141-payer-resolution`). Verified against the live merged EIP
(`ethereum/EIPs@master`, EIP-8141 Mempool section — Acceptance Algorithm γρ.904,
Replacement and Eviction γρ.915, Revalidation γρ.925, canonical/non-canonical paymaster
`reserved_pending_cost` γρ.876–902) and against the payer-resolution layer already on this
branch (`FrameTxPayerResolver` / `FrameTxPayerFilter` / `Transaction.PayerAddress`).

## Context

The payer-resolution layer (`PAYER-RESOLUTION-DESIGN.md`) resolves *who pays* a frame
transaction at admission and records it on `Transaction.PayerAddress`, but it is an annotation
step that never rejects. Every enforceable public-mempool DoS rule was deferred there. This
design enumerates that deferred rule set and implements the smallest coherent, correct slice
of it.

## The full public-mempool rule set (from the merged spec)

1. **Per-payer exposure accounting** (γρ.921). "Reservation accounting applies to every payer,
   not only canonical paymasters. A node MUST NOT hold pending frame transactions whose summed
   maximum costs (`TXPARAM(0x06)`) exceed the payer's balance", tracked per payer exactly as
   `reserved_pending_cost` is for a canonical paymaster. `available = balance(payer) −
   reserved_pending_cost(payer)`; reject a new frame tx if `available < tx.max_cost`. Increment
   on admission; decrement on eviction, replacement, inclusion, or reorg removal.
2. **Canonical-paymaster reservation** (γρ.876–886). Same shape but the "balance" is
   `state.balance(pm) − reserved − pending_withdrawal_amount(pm)`, and the paymaster is
   recognized by runtime-code-hash match. **Blocked upstream:** the canonical paymaster code
   hash is not yet pinned in the spec nor in `Eip8141Constants`, so no `pay` frame resolves to a
   canonical paymaster today (all → `RequiresSimulation`). Deferred.
3. **Non-canonical paymaster cap** (γρ.888–902). A `pay` frame whose target carries code that is
   *not* the canonical implementation may be used by at most
   `MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER` (= 1) pending txs, plus a balance check.
   Deferred (needs code-carrying-target resolution = the simulation layer).
4. **Replacement** (γρ.917–919). Identity is `(sender, nonce)`; a replacement must be
   independently valid and bump both `max_fee_per_gas` and `max_priority_fee_per_gas` by a
   node-configured minimum; **may switch payer**, in which case the old payer's reservation is
   released and the new payer's taken *atomically*. Deferred.
5. **Eviction ordering** (γρ.923). On resource pressure evict: invalidated-against-head first,
   then nearest expiry deadline, then lowest effective priority fee; released reservations;
   evicted/replaced txs not re-propagated. Deferred.
6. **Revalidation** (γρ.925). On a new head, drop included txs, then re-simulate only the txs
   whose tracked dependencies (incl. **payer balance/code** — the #12007 alignment) were touched.
   Needs dependency-set **indexing** (`FrameTxDependencySet` is captured but not indexed).
   Deferred.
7. **`MAX_VERIFY_GAS` admission bound** (γρ.782, 812). Sum of validation-prefix `gas_limit` plus
   signature-verification intrinsic cost ≤ `MAX_VERIFY_GAS` (100_000). Deferred.
8. **Tighten `NotSupportedTxFilter` TEMPORARY gate.** It currently admits frame txs under the
   active fork unconditionally (`EIP8141-GAP TEMPORARY`); the gate should require the DoS filters
   above before frame txs are accepted from the *public* mempool. Deferred until the set is
   complete.

## Chosen slice: per-payer exposure accounting (rule 1)

Rule 1 is the primary consumer of the payer this branch already resolves, is self-contained, and
needs no simulation, no upstream-pinned constant, and no new pool-lock coordination — the reservation
is made atomic with a per-key compare-and-set in the cache rather than a pool lock. It is the
smallest slice that turns the annotation into an actual admission gate.

### Mechanism

- **`PayerExposureCache`** (new, internal) — a `ConcurrentDictionary<AddressAsKey, UInt256>`
  summing the pending max-cost reserved per resolved payer. `GetReserved`, `TryReserve` (atomic
  conditional reserve), and `Subtract` (release, with the same thread-safe remove-when-zero idiom
  as `DelegationCache`).
- **`FrameTxPayerExposureFilter`** (new `IIncomingTxFilter`) — placed *last* in the post-hash
  pipeline, after `FrameTxPayerFilter` (which sets `PayerAddress`). For a frame tx with a resolved
  payer it reads the payer's head balance and calls `TryReserve(payer, maxCost, balance)`, which
  atomically reserves the cost iff `reserved + maxCost ≤ balance`, rejecting (`PayerExposureExceeded`)
  otherwise. Reserving inside the admission check — rather than reading the total and reserving later
  — closes the check-then-act race whereby N concurrent same-payer submissions (P2P + RPC threads run
  `SubmitTx` under a *shared* read lock) could all observe a pre-reservation total and all pass.
  Non-frame txs and unresolved (`RequiresSimulation` / `NoPayer`, i.e. `PayerAddress is null`) frame
  txs pass through untouched — enforcement is scoped to natively-resolved payers.
- **Release** — the reservation is released via the existing `TxDistinctSortedPool.Removed` handler
  (`OnRemovedTx`), which covers eviction, replacement, inclusion, and reorg removal (they all funnel
  through pool `Removed`), plus the one admission path that reserves but never inserts
  (`ReplacementNotAllowed` in `AddCore`, which raises no event), compensated explicitly there.
- **Max cost** = the pool's existing `Transaction.IsOverflowInTxCostAndValue(out cost)`. For a frame
  tx this reduces to **gas only** (`MaxFeePerGas·GasLimit`): the top-level `Value` is always zero
  (frame value lives on `TxFrame.Value`, the frame-tx decoder has no top-level `value` field) and the
  blob term never contributes because blob fields are rejected at validation while frame-blob support
  is off (see the blob note below). This is a sound proxy for `TXPARAM(0x06)`; it omits the
  signature-verification intrinsic add-on, which lands with the `MAX_VERIFY_GAS` slice. On overflow
  the tx is rejected (`Int256Overflow`).

### Why not fold it into `FrameTxPayerFilter`

Keeping resolution (annotation, always-accept) and exposure (gate, can reject) as separate
filters mirrors the resolver/`DelegatedAccountFilter` split and keeps each filter single-purpose;
the resolver stays reusable by the deferred simulation layer unchanged.

## Correctness / scope notes

- **Replacement over-count (accepted for this slice).** During a same-`(sender,nonce)` replace,
  the old tx is still pending when the new one is checked, so its cost is still counted in
  `reserved` — the check is conservative (may reject a valid replacement that switches to/keeps a
  near-cap payer). Atomic release/take is **rule 4**, explicitly deferred. Documented as an
  `EIP8141:` follow-up.
- **Blob-carrying frame txs.** Frame-blob support is a separate deferred track, so the frame-tx
  validator now includes `NonBlobFieldsTxValidator`, rejecting any frame tx that carries
  `max_fee_per_blob_gas` or `blob_versioned_hashes`. This keeps the gas-only max-cost proxy exact:
  a blob-carrying frame tx can no longer reserve ≈ gas-only exposure while smuggling an unbounded
  blob term past the gate. When frame-blob support lands, the blob term must be added to the
  reserved max cost (option (b)) at the same time the validator gate is relaxed.
- **Balance freshness.** The filter reads head balance at admission; keeping `reserved` correct
  as balances move on head changes is **rule 6** (revalidation/indexing), deferred. This slice
  bounds exposure at admission time, which is the spec's admission-side MUST.

## Staging

**This PR (slice):** `PayerExposureCache` + `FrameTxPayerExposureFilter` + wiring into the
post-hash pipeline and the insert/remove accounting + a new `AcceptTxResult.PayerExposureExceeded`
reason + tests.

**Deferred (documented `EIP8141:` follow-ups referencing this doc + ethereum/EIPs#12007):**
- Canonical-paymaster reservation (rule 2) — blocked on the upstream code-hash pin.
- Non-canonical paymaster cap (rule 3) — needs the simulation layer.
- Replacement payer-switch atomicity (rule 4).
- Eviction ordering (rule 5).
- Dependency-indexed revalidation incl. payer balance/code triggers (rule 6).
- `MAX_VERIFY_GAS` admission bound (rule 7).
- Tightening the `NotSupportedTxFilter` TEMPORARY gate (rule 8).

## Open fork for Marc

- **Default-code sponsor classification (#12007 H3).** The merged text says a `pay` frame whose
  target has the empty code hash (a default-code EOA sponsor) is governed by per-payer exposure
  alone, not the cap. This slice implements exactly that reading: resolved default-code sponsors
  get exposure accounting; code-carrying paymasters stay `RequiresSimulation` and get no gate yet.
  If the classification shifts upstream (cap=1 for EOA sponsors), the change is additive.
