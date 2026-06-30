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
| 4 | `rpcchainvm` gRPC VM server | `Initialize/BuildBlock/ParseBlock/GetBlock/SetState/Verify/Accept/Reject/SetPreference/Health/Version/...` | 🟡 **`Nethermind.Avalanche.Vm` builds clean + handshake e2e test passes**: reverse-gRPC handshake (proto 45), full `vm.VM` service + rpcdb client. `ParseBlock` decodes via the codec; `BlockVerify` enforces the `ExtDataHash` invariant. `BuildBlock`/`BlockAccept`/`BlockReject` + execution-based verify remain stubbed | block lifecycle remaining |
| 5 | Externally-driven block lifecycle | Snowman decides acceptance; VM has no fork choice | ❌ (Nethermind drives its own fork choice today) | large |
| 6 | C-Chain block format | Coreth `extblock` + header (incl. `ExtDataHash`, AP4 + Granite optionals) | ✅ **RLP codec implemented + VALIDATED byte-exact against 3 real mainnet blocks** (AP4/Cancun/Granite eras): `ComputeHash` = `keccak256(RLP(header))` reproduces each network block hash | done |
| 7 | Dynamic fees | AP3 dynamic base fee, AP4/AP5 changes, Etna fee config, ACP-176 gas target | ❌ | medium |
| 8 | Atomic transactions | C↔X/P import/export via shared-memory atomic UTXOs | ❌ | large |
| 9 | Coreth precompiles & stateful contracts | native-asset-call/balance (legacy), warp messaging | ❌ | medium |
| 10 | State sync (`StateSyncableVM`) | snapshot served/consumed every 4000 blocks, 256-block backfill for `BLOCKHASH` | ❌ (Nethermind snap/fast-sync does NOT transfer) | **large** |
| 11 | Genesis | Avalanche C-Chain genesis + allocations | ❌ | small |
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
- **`Nethermind.Avalanche.Vm`** rpcchainvm server — reverse handshake (proto 45, e2e test), `ParseBlock` decodes via the
  codec, `BlockVerify` enforces the `ExtDataHash` consensus invariant, rpcdb client adapter.
- `SealEngineType.Avalanche`; wired into `NethermindPlugins`, `Nethermind.Runner`, `Nethermind.slnx`.
- **Compiles cleanly** (.NET SDK 10.0.300, `0 warnings / 0 errors`); `Nethermind.Avalanche.Test` 61/61 + handshake 1/1 pass.

### Bottom line
The chain-recognition, fork/spec framework, **state-root parity, block/header codec (real-mainnet-validated), and the
rpcchainvm server with `ParseBlock`/`BlockVerify` wired** are in place. **Remaining for a working C-Chain validator:
items #5, #7, #8, #10, #11 plus the rest of the block lifecycle** (`Initialize` bootstrap of the Nethermind world state +
genesis, `BuildBlock`, `BlockAccept`/`BlockReject`, and execution-based `BlockVerify`). Realistic order:
`Initialize` bootstrap → BuildBlock/Accept/Reject + execution verify → fee mapping → state sync → atomic txs → precompiles.

## Sync-speed comparison

A head-to-head "is Nethermind faster than Coreth at syncing the C-Chain" benchmark requires
items #4/#5/#10 working (the VM must actually sync). Until then it is **not measurable** —
there is no Nethermind Avalanche sync to time.

What we can record now is the **AvalancheGo baseline** on this VM (16 vCPU / 62 GB /
NVMe-backed `/mnt/sda`), mainnet, C-Chain state-sync + pruning enabled. See
`SYNC_BASELINE.md` (updated as the run completes) for per-chain (P/X/C) bootstrap timings.
That baseline is the reference any future Nethermind-VM comparison must beat.
