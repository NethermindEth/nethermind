# Daisugi: what Nethermind needs so they can drop their patches

Date: 2026-10-29. Sources: `asanso/AA-PQ@a4b1d73` (main), `NethermindEth/nethermind` branches, public RPC `https://daisugi.fyi/rpc`.

## TL;DR

- Daisugi runs **our `feature/eip8288-lean` code frozen at `4a17b571`** (2026-10-04, Marc), plus two overlays: `consensus/fork` (~57 lines) and `adaptive` (~600 lines + native leanVM overlay + Rust worker).
- Our branch tip (`d6782c195e`, 2026-10-21) is **778 commits ahead** and **no longer speaks the Daisugi protocol**. Removing their patches by rebasing onto tip is impossible without breaking their chain history.
- Overlays fall into three groups:
  1. **Real bugs in our branch**: upstream as fixes.
  2. **Daisugi-specific deviations from the EIP**: decide EIP change vs. generic chainspec knob.
  3. **Adaptive aggregation**: tip already has the generic per-fork VK mechanism; the batching policy is the remaining client feature.

## Branches

| Branch | Status |
| --- | --- |
| `feature/eip8288-lean` | Main 8288 branch. Contains the Daisugi pin as an ancestor. |
| `feature/eip8288-leanvm-align` | Side branch, also contains the pin. Not "our client". |
| Pin `4a17b571` | "Avoid redundant aggregation work and recover rejected cache entries". Last commit before the protocol moved. |

## Live network facts (derived, read-only)

| Item | Value | Source |
| --- | --- | --- |
| Chain ID | 1337 | RPC |
| Genesis hash | `0x3f08cc…3e55` | RPC, docs |
| Genesis timestamp | 1789235743 | RPC block 0 |
| `eip8141PrototypeTime` | 1790274781 (block 519324) | `docs/NETHERMIND-DEPLOYMENT.md`, RPC |
| `eip8288PrototypeTime` | **1791238303** (block 1000889) | Inferred: Daisugi CL fork epoch 31290 × 64 s + genesis; first block lands exactly on it |
| `daisugiLegacyFrames` | true | Your paste; `ACTIVATION.md` (`--preserve-native-wallet`) |
| `daisugiAdaptiveAggregationTime` | Unknown, probably unset | Adaptive README: "no timestamp selected" |
| Client | `Nethermind/v2.2.0-preview-hpA` | `web3_clientVersion` |
| Topology | **Single EL + single Lighthouse**, discovery off | `deploy/nethermind/run-node.sh` |

- No bootnodes: nothing to peer with. Running our node next to theirs requires their cooperation (static peer, or a beacon node feeding us).
- Full `genesis.json` (allocs) is not public: `/opt/aa-pq/migration-nethermind-20260924/genesis-frames.json` on their host. Need to ask.

## Protocol drift: pin vs. our tip

All consensus-breaking for Daisugi history:

| Change on tip | Commit | Effect on Daisugi |
| --- | --- | --- |
| `get_deps_hash`: Keccak over 96-byte triples → BLAKE2s, scheme-bound | `d71d3858dc`, `353a2c5f9b` (EIPs#12474) | Every historical proof commitment differs |
| Recursive proof moved header → block body | `44912aa9df` | Block/header RLP and hash differ |
| `AGGREGATED_VK` `9370d7…` → `00f24b…`, leanVM `854997b` → `cf1e92f`, native ABI 5 → 6 | same | Old proofs need the old guest and library |
| `lean/1` → `eth/73` proof wrapper propagation | `30113df6e1` | Not consensus; irrelevant for a single node |
| Frame tx decoder, header decoder changes | various | Not verified individually |

The AA-PQ docs confirm it: "This commitment format differs from the evolving format in EIPs PR #12417."

## Overlay 1: `consensus/fork` (in production)

| # | Change | Kind | Status on our tip | Proposed action |
| --- | --- | --- | --- | --- |
| 1 | `Eip8288Prototype` stops setting `IsEip8141Enabled = true`. Otherwise the 8141 label gets rescheduled to the 8288 timestamp | **Bug in our code** | Still present | Upstream fix + regression test. Their test run shows our own spec test asserts the buggy behavior |
| 2 | `HeaderValidator`: accept `SlotNumber == 0` placeholder when a later optional header field (recursive proof) is present | Bug (optional-field RLP padding) | Moot on tip (proof moved to body) | Only needed for a Daisugi-compatible line |
| 3 | `engine_getPayloadBodiesByHash/Range` V1/V2 return `recursiveStarkProof` + `recursiveStarkBlockDepsHash`; SSZ REST rejects them | Bug: Lighthouse can't backfill proofs | Tip has `StarkProof` on body results (proof now lives in body) | Check field name and shape match their Lighthouse; probably covered |
| 4 | `daisugiLegacyFrames`: keep scalar-nonce frame txs valid after EIP-8250 (scalar nonce = keyed `[0]`) | **Consensus deviation from EIP** | Tip rejects (`LegacyNonceNotAllowed`) | EIP question for 8250/8288: transition path for existing scalar-nonce wallets. Otherwise a generic chainspec knob, not a chain-1337 check |
| 5 | `MAX_VERIFY_GAS` 300k → 500k (SPHINCS-G witness verification) | Admission only, not consensus | Tip still 300k constant | Make it a chainspec/config param, or raise in the EIP. Real feedback: 300k is too low for their PQ signer |

## Overlay 2: `adaptive` (opt-in, likely not live)

- Second guest program (VK `2db957…`), versioned verifier, switch at `daisugiAdaptiveAggregationTime`.
  - **Tip already has the generic version**: per-fork `Eip8288AggregatedVk` + `eip8288AggregatedVkTimestamp` (`e807522a70`). Their bespoke `VersionedLeanProofVerifier` maps onto it.
- Batching policy: up to 40 claims per direct batch (vs. 4), slot-aware dispatch, 6 s group retention, arrival-order selection.
  - Pure block-production policy. Candidate for a generic, configurable production feature.
- Optimized leanVM guest + out-of-process prover (`daisugi-lean-worker`). This is Marc's area (leanVM fork).

## Options for "no patches"

| Option | What it takes | Trade-off |
| --- | --- | --- |
| A. Maintained `daisugi` branch off `4a17b571` with overlay 1 as reviewed commits (adaptive later) | Small. ~57 lines + tests | They stop patching; we own a frozen branch. Diverges from tip forever |
| B. Daisugi upgrades to the current protocol at a future fork | Tip must still validate pre-fork history (old deps hash, header-proof, old VK/ABI) | Two protocol versions in one client. Large, and old-format DB blocks must decode |
| C. Daisugi re-genesis on tip protocol | None on our side beyond fixes 1, 4, 5 | Their docs say "do not reset genesis". Their call |

Recommendation: **A now** (unblocks them immediately, small diff), and push **1, 4, 5** to tip/EIP so any future Daisugi upgrade or re-genesis needs no patches.

## Work items for us

- [ ] Fix `Eip8288Prototype` rescheduling EIP-8141 (tip). Regression test with `eip8141PrototypeTime < eip8288PrototypeTime`.
- [ ] Make `MaxVerifyGas` configurable per chain (tip). Raise EIP feedback on 300k vs. SPHINCS-G.
- [ ] Decide scalar-nonce compat after EIP-8250: EIP text or generic chainspec flag (tip).
- [ ] Verify tip payload-body proof field matches their Lighthouse JSON (`recursiveStarkProof`, `recursiveStarkBlockDepsHash`).
- [ ] If option A: create `daisugi` branch from `4a17b571`, port overlay 1 as commits, replace `ChainId == 1337` gate with plain chainspec flags.

## Open questions (to ask)

- Daisugi team: full `genesis-frames.json`; is `daisugiAdaptiveAggregationTime` set live; acceptable to run a long-lived frozen branch, or do they plan to follow EIP changes?
- Marc: is option B ever planned (multi-version 8288 in one client)? Who owns the leanVM `854997b` guest for Daisugi?
- Marcos: what did the deployment you saw use (same pin + overlays?).

## Update 2026-11-02: full AA-PQ repo read

### Chain history has three rule eras

| Blocks | Rules | Client used |
| --- | --- | --- |
| 0 – 519323 | Prague, no frames (Geth until 519010) | Geth, then NM `c9ad4b5` |
| 519324 – 1000888 | EIP-8141 devnet7 frames: 7-field scalar-nonce envelope | NM `c9ad4b5` + `tohex-prefix` + `verify-500k` |
| 1000889 – now (2.19M) | Daisugi fork: 8288 at `4a17b571`, proof tuple in header field 23 | NM `4a17b571` + `consensus/fork` overlay |

- A client that syncs from genesis must apply each era's rules to its blocks.
- Two wallet generations live on chain: devnet7 scalar-nonce accounts (6176-byte SPHINCS in the tx) and 8288 aggregate accounts (proof wrapper). `daisugiLegacyFrames` keeps the first kind working.
- Deployed account bytecode hardcodes opcodes `0xaa`, `0xb0`–`0xb6` and field indexes. Still the same numbers on our tip.

### Client behavior their stack depends on

- RPC: `eth_sendProofWrapper` / `eth_getProofWrapper` (exact error strings matched), type-0x06 `eth_call` object, receipts with `frameReceipts[]` (status 0/1/2) and `payer`, `debug_getRawBlock` (explorer parses header field 23).
- Engine: `newPayloadV4` / `forkchoiceUpdatedV3` / `getPayloadBodiesV1` with `recursiveStarkProof` + `recursiveStarkBlockDepsHash`. Empty blocks carry a 12-byte `NLR3` envelope.
- Alto: `debug_traceCall` JS tracer needs `0x`-prefixed `toHex`. Fixed upstream in #13807, already in our tip, missing in `4a17b571`.
- Operator settings: `TxPool.FrameTxMaxVerifyGas=500000`, `Pruning.Mode None`, discovery off, single node.

### People (from git history)

- Giulio (`Giulio2002`): Nethermind migration, EF host.
- GianlucaFelli: 8288 fork and adaptive commits.
- asanso: repo owner.

## Not verified

- Did not build or run either overlay or the tip against Daisugi blocks (no raw-block RPC is public; `debug_getRawBlock` is private).
- `eip8288PrototypeTime` is inferred from the CL epoch, not read from their config.
- Did not diff frame-tx encoding pin vs. tip line by line.
- Overlay diffs reproduced in a scratch worktree: overlay files copied onto `4a17b571`, not via their `apply.py`.
