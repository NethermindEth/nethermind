# Nethermind on Avalanche — Coreth parity & status

This documents how close `Nethermind.Avalanche` is to a **feature-complete** Avalanche
C-Chain node, measured against the reference implementation (**AvalancheGo + Coreth**, Go),
and what a sync-speed comparison would require.

## Architecture target

Avalanche chains run as **VM plugins** that AvalancheGo drives over the `rpcchainvm`
gRPC interface (protocol v45 in AvalancheGo v1.14.2). AvalancheGo owns consensus
(**Snowman**), networking, and the P-Chain validator set; the VM only executes blocks
when told to. So "Nethermind on Avalanche" = **Nethermind's EVM packaged as the C-Chain VM**,
bug-for-bug compatible with Coreth. X-Chain (UTXO DAG) and P-Chain (staking) are not EVM
and remain AvalancheGo's responsibility.

## Status legend
- ✅ present (reused from Nethermind core) · 🟡 partial / scaffolded · ❌ missing (follow-on work)

| # | Subsystem | What Coreth/AvalancheGo provides | Nethermind status | Effort |
|---|-----------|----------------------------------|-------------------|--------|
| 1 | EVM execution | Geth EVM | ✅ Nethermind EVM (mature) | reuse |
| 2 | Account/state model + MPT | Geth state + snapshot | ✅ Nethermind state/trie + **Coreth state-root parity primitives implemented, 22/22 byte-exact tests pass** (5-field `isMultiCoin` account RLP, storage-key bit0 transform, `ExtDataHash`) | parity layer done |
| 3 | Fork schedule → EIP mapping | Apricot 1–6, Banff, Cortina, Durango, Etna, Fortuna, Granite | ✅ **implemented + builds clean**: Berlin/London by block; Durango=Shanghai, Etna=Cancun-subset, Granite=P256VERIFY by timestamp; divergences encoded (no blobs/withdrawals/beacon-root) | done |
| 4 | `rpcchainvm` gRPC VM server | `Initialize/BuildBlock/ParseBlock/GetBlock/SetState/Verify/Accept/Reject/SetPreference/Health/Version/...` | 🟡 **`Nethermind.Avalanche.Vm` builds clean + handshake e2e test passes**: reverse-gRPC handshake (proto 45), full `vm.VM` service + rpcdb client. `Initialize` reconstructs genesis from `genesis_bytes` and returns the correct last-accepted; `ParseBlock` decodes via the codec; `BlockVerify` enforces the `ExtDataHash` invariant. Executing world state, `BuildBlock`/`BlockAccept`/`BlockReject` + execution-based verify remain | block lifecycle remaining |
| 5 | Externally-driven block lifecycle | Snowman decides acceptance; VM has no fork choice | ❌ (Nethermind drives its own fork choice today) | large |
| 6 | C-Chain block format | Coreth `extblock` + header (incl. `ExtDataHash`, AP4 + Granite optionals) | ✅ **RLP codec implemented + VALIDATED byte-exact against 3 real mainnet blocks** (AP4/Cancun/Granite eras): `ComputeHash` = `keccak256(RLP(header))` reproduces each network block hash | done |
| 7 | Dynamic fees | AP3 dynamic base fee, AP4/AP5 changes, Etna fee config, ACP-176 gas target | ❌ | medium |
| 8 | Atomic transactions | C↔X/P import/export via shared-memory atomic UTXOs | ❌ | large |
| 9 | Coreth precompiles & stateful contracts | native-asset-call/balance (legacy), warp messaging | ❌ | medium |
| 10 | State sync (`StateSyncableVM`) | snapshot served/consumed every 4000 blocks, 256-block backfill for `BLOCKHASH` | ❌ (Nethermind snap/fast-sync does NOT transfer) | **large** |
| 11 | Genesis | Avalanche C-Chain genesis + allocations | ✅ **`AvalancheCChainGenesis` parses `cChainGenesis`, reproduces mainnet block 0 byte-exact**: state root `0xd65eb1b8…29cc` + block hash `0x31ced5b9…96b` (5-field accounts) | done |
| 12 | `eth_*` JSON-RPC via `/ext/bc/C/rpc` | full eth API surface | 🟡 Nethermind has `eth_*`; needs routing through the VM host, not its own server | medium |
| 13 | DB layout / acceptance semantics | versioned/atomic commit keyed to Snowman accept | ❌ | medium |

### Done so far (this fork)
- `AvalanchePlugin` (`IConsensusPlugin`) activated by chainspec `engine.avalanche`.
- `AvalancheChainSpecEngineParameters` — C-Chain upgrade-timestamp parameters.
- `IAvalancheReleaseSpec` / `AvalancheReleaseSpec` — per-fork flags.
- `AvalancheChainSpecBasedSpecProvider` — fork-aware spec selection.
- **State-root parity primitives** — 5-field `isMultiCoin` account RLP, storage-key bit0 transform, `ExtDataHash`.
- **Coreth `extblock` + header RLP codec** — `AvalancheBlockHeader`/`AvalancheHeaderDecoder`/`AvalancheBlockDecoder`,
  Granite-complete (all 8 trailing optionals incl. `TimeMilliseconds`/`MinDelayExcess`), block hash = `keccak256(RLP(header))`.
  **Validated byte-exact against 3 real mainnet C-Chain blocks** (AP4 5,000,000 / Cancun 70,000,000 / Granite 89,117,142).
- **`AvalancheCChainGenesis`** — parses the `cChainGenesis` JSON and reproduces mainnet block 0 byte-exact
  (state root `0xd65eb1b8…29cc`, block hash `0x31ced5b9…96b`).
- **`Nethermind.Avalanche.Vm`** rpcchainvm server — reverse handshake (proto 45, e2e test), `Initialize` returns the
  reconstructed genesis as last-accepted, `ParseBlock` decodes via the codec, `BlockVerify` enforces the `ExtDataHash`
  consensus invariant, rpcdb client adapter.
- `SealEngineType.Avalanche`; wired into `NethermindPlugins`, `Nethermind.Runner`, `Nethermind.slnx`.
- **Compiles cleanly** (.NET SDK 10.0.300, `0 warnings / 0 errors`); `Nethermind.Avalanche.Test` 66/66 + handshake 1/1 pass.

### Target: standalone sync of the mainnet C-Chain
The standalone, "the-way-they-do-it" architecture is **Nethermind as the rpcchainvm VM, driven by AvalancheGo**
(one node = AvalancheGo + the VM plugin, exactly like AvalancheGo + Coreth — AvalancheGo owns networking/Snowman/bootstrap
and feeds blocks via `ParseBlock → Verify → Accept`). AvalancheGo hardwires the mainnet C-Chain to in-process Coreth, so
the real C-Chain additionally requires a **small AvalancheGo patch** registering the Nethermind plugin under the EVM VM ID.

### The gating hard problem: executing-state parity
Genesis parity uses a raw trie with the Avalanche encoder. For *sync*, every executed block's post-state root must match
Coreth, which means Nethermind's **live world state** must encode accounts the 5-field way and apply the storage-key bit0
transform. `StateTree` uses a `static` non-virtual `AccountDecoder` (4-field) and the hot read path uses `AccountStruct` +
the flat-state DB — so this is a **fork-level state-layer change**, not a subclass: an `isMultiCoin`-aware account
encode/decode wired through `StateTree`, the storage tries, and the flat-state read path (`isMultiCoin` is always `false`
on the post-Apricot C-Chain, so in practice a deterministic `+0x80` on every account leaf). This is the single largest
remaining piece and gates execution-based `BlockVerify`/`BlockAccept`.

### Bottom line
Done: chain/fork/spec framework, **state-root parity primitives, block/header codec (real-mainnet-validated), genesis
parity, and the rpcchainvm server with `Initialize`/`ParseBlock`/`BlockVerify` wired**. Remaining for a working C-Chain
sync, in order: **(1) executing-state parity** (above) → **(2) execution-based `BlockVerify` + `BlockAccept`/`Reject` +
persistence + `GetBlock`/`SetPreference`** → **(3) AvalancheGo patch** (EVM-ID → plugin) → **(4) per-fork dynamic fees**
→ **(5) atomic transactions** (C↔X/P imports change C balances) → **(6) state-sync** (else a from-genesis replay of ~89M
blocks). Items 1, 5, 6 are individually large; a full mainnet sync is a multi-month effort. Each lands as a verifiable
milestone — genesis (block 0) is the first, and the foundation through `Initialize` is in place.

## Sync-speed comparison

A head-to-head "is Nethermind faster than Coreth at syncing the C-Chain" benchmark requires
items #4/#5/#10 working (the VM must actually sync). Until then it is **not measurable** —
there is no Nethermind Avalanche sync to time.

What we can record now is the **AvalancheGo baseline** on this VM (16 vCPU / 62 GB /
NVMe-backed `/mnt/sda`), mainnet, C-Chain state-sync + pruning enabled. See
`SYNC_BASELINE.md` (updated as the run completes) for per-chain (P/X/C) bootstrap timings.
That baseline is the reference any future Nethermind-VM comparison must beat.
