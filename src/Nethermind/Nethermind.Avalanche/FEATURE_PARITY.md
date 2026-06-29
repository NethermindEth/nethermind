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
| 4 | `rpcchainvm` gRPC VM server | `Initialize/BuildBlock/ParseBlock/GetBlock/SetState/Verify/Accept/Reject/SetPreference/Health/Version/...` | 🟡 **`Nethermind.Avalanche.Vm` builds clean**: reverse-gRPC handshake (proto 45), full `vm.VM` service + rpcdb client; block-lifecycle RPCs stubbed | block lifecycle remaining |
| 5 | Externally-driven block lifecycle | Snowman decides acceptance; VM has no fork choice | ❌ (Nethermind drives its own fork choice today) | large |
| 6 | C-Chain block format | Coreth block wrapping under proposervm/Snowman++ | ❌ | medium |
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
- `SealEngineType.Avalanche`; wired into `NethermindPlugins`, `Nethermind.Runner`, `Nethermind.slnx`.
- **Compiles cleanly** (.NET SDK 10.0.300, `0 warnings / 0 errors`).

### Bottom line
The scaffold establishes the chain-recognition and fork/spec framework inside Nethermind's
plugin model. **Feature parity with Coreth is gated on items #4, #5, #8, #10** (the gRPC VM
server, externally-driven acceptance, atomic txs, and the Avalanche state-sync protocol),
which are the substantial follow-on. Realistic order: VM gRPC server → block lifecycle →
fork/EIP + fee mapping → state sync → atomic txs → precompiles.

## Sync-speed comparison

A head-to-head "is Nethermind faster than Coreth at syncing the C-Chain" benchmark requires
items #4/#5/#10 working (the VM must actually sync). Until then it is **not measurable** —
there is no Nethermind Avalanche sync to time.

What we can record now is the **AvalancheGo baseline** on this VM (16 vCPU / 62 GB /
NVMe-backed `/mnt/sda`), mainnet, C-Chain state-sync + pruning enabled. See
`SYNC_BASELINE.md` (updated as the run completes) for per-chain (P/X/C) bootstrap timings.
That baseline is the reference any future Nethermind-VM comparison must beat.
