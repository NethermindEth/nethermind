# Avalanche mainnet sync baseline (AvalancheGo reference)

Reference timings for syncing **Avalanche mainnet, all 3 chains**, captured on the project VM.
This is the baseline a future Nethermind C-Chain VM sync would be compared against (see
`FEATURE_PARITY.md` — a Nethermind-vs-Coreth sync benchmark is not possible until the VM
integration can actually sync).

## Environment
- Host: 16 vCPU, 62 GB RAM, NVMe-backed `/mnt/sda` (data dir `/mnt/sda/avalanche/data`)
- Client: **AvalancheGo v1.14.2** (Coreth C-Chain VM built in), network `mainnet`
- C-Chain config: `state-sync-enabled: true`, `pruning-enabled: true`
- Bootstrap order is P → X → C; the node is "synced" when `info.isBootstrapped` is `true`
  for all three and `/ext/health` reports healthy.

## Timeline (UTC)
| Phase | Start | End | Notes |
|-------|-------|-----|-------|
| Node launch | 17:41:51 | — | tmux session `ago` |
| P-Chain block-fetch | 17:41:58 | (in progress) | 25,142,493 blocks total; 94% by 18:32 (~50 min projected) |
| P-Chain execute/bootstrap | — | — | follows fetch |
| X-Chain bootstrap | — | — | |
| C-Chain state-sync + bootstrap | — | — | downloads recent state snapshot + 256-block backfill |
| All 3 bootstrapped + healthy | — | — | **goal completion** |

_This file is updated as each milestone is reached._

## Method notes
- Progress source: AvalancheGo structured logs (`/mnt/sda/avalanche/logs/run.log`,
  `P.log`, etc.) and `info.isBootstrapped` per chain.
- P-Chain block-fetch is the first long phase; it is followed by block execution, then the
  X and C chains. C-Chain uses state-sync, so it does not replay full history.
