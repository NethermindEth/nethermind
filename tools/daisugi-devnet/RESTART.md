# Daisugi fresh-genesis restart: operator handoff

The F128 stack this kit pins, how it was validated, and how operators bring up a shared network from it. Build and genesis commands live in [README.md](README.md); this file adds the frozen pins, the multi-host steps and the health checks.

## Frozen stack

| Component | Revision | Check |
| --- | --- | --- |
| Nethermind | `fe94447c770f7eedfb31336c47a824df69aeaf53` (`feature/eip8288-lean` `757b7eabed` + NethermindEth/nethermind#15955) | `nethermind --version` prints `Commit: fe94447c…` |
| Native Lean backend | `Nethermind.Crypto.Lean` 0.21.0.110 (int.nugettest.org), from NethermindEth/lean-bindings `1209c17` | nupkg sha256 `daecd150e646cdbc1ecaffb0fa118dff780c8a928a2ff705a7c3362fddd51ac2` |
| Native library in the image | `/nethermind/libnethermind_lean.so` | sha256 `aad07d8fa0f106457ef1719eadd1685a98e39fe6df3ab27308022e14637b8f00` (x64), `caa3ed9b725d44cef6411452f35342f50f2d077dfe8dd666f4360abfd436c8ad` (arm64, not run) |
| Protocol | leanVM `f7ba4999` (F128), native ABI 21, leanSPHINCS key `PK.seed \|\| PK.root` (ethereum/EIPs#12666) | `AGGREGATED_VK` `e09e5acfcfe87832f337eb42acac98246d160bb915b55c55fbd8118b203cbb81` |
| Lighthouse | v8.2.2 `e423a667` + asanso/AA-PQ#10 patches `0ba239e` | `/lighthouse` sha256 `109c9a91e128db8cb17be6939c5f614b1ae494a68da53814e9300bc5299ccd3d` |
| PQ signer (`FX`) | `testing/runtimes/linux-x64/native/fixtures` of the same nupkg | sha256 `fc25affec68cbafacd35a728a7f665bd6aa75cbe0e59b7fc0775d4f47386b982` |
| AA-PQ wallet | asanso/AA-PQ#12 `9f40934` (`eip8288-lean` profile, `PK.seed \|\| PK.root`); guide #13 `f4932aa` | lean runtime hash `0x0b882e19…b467`, factory creation hash `0x20b2c680…5462` |

- Every EL must run this exact Nethermind build. A node with another native build reports a different `AGGREGATED_VK`, refuses to start or rejects every proof.
- Rebuilt images get new local IDs; compare the commit and library hashes above, not image IDs.
- Every EL on the network must be Nethermind: it follows open EIP drafts (proof in the block body, #12479; proof-carrying inclusion lists, #12475; wrapper limits, #12590) that EIP-8288 master does not have yet.

## Validation (2026-11-16, one host, Kurtosis)

- 3 Nethermind + Lighthouse + validator nodes, 64 validators each, fresh genesis, Daisugi fork at epoch 3 (`kurtosis/run.sh`, `NODES=3`, `PQ_SCHEMES="sphincs stark mixed"`).
- `check.sh`: EL/CL genesis agree, Lean backend up, finality past the fork, 0 missed slots on slots 96..192, 0 error lines on EL, beacon and validator.
- `pq.sh`, one series per scheme (`0x10`, `0x11`, mixed): every valid wrapper included with status `0x1`; corrupted witness, early keyed nonce, replay and stale sequence all rejected.
- Burst of 8 wrappers (sphincs, stark, mixed) sent across the 3 nodes: all included in 3 blocks; one block proof merged 2 recursive children.
- Block proofs 213 to 279 KB, under the 1.25 MiB bound.
- Late joiner (`kurtosis/sync.sh`) synced from genesis over P2P; `sync-check.sh` on 4 ELs and 4 CLs: identical hash, state root and receipts root for every block, identical raw blocks, receipts and `starkProof` for 6 proof blocks, same finalized head.
- AA-PQ: lean factory deployed; a fresh NiceTry SPHINCS-G key created its account through its first transaction at the wallet-derived address and paid a recipient; a witness in the old `root || seed` order was rejected; portal, RPC gateway (HTTP and HTTPS front) and explorer answered.
- Not validated: multi-host networking, public bootnodes, arm64.

## Restart procedure

1. Build or pull the images of [README step 1](README.md#1-build-the-images). Check the hashes in "Frozen stack" on every host.
2. Generate the genesis once, on one trusted host, as in [Shared network](README.md#shared-network): a new `CHAIN_ID`, a private mnemonic file (mode 0600), `OPERATORS` with each operator's validator range, a genesis timestamp far enough ahead for distribution.
3. Distribute to every operator: `params.json`, `data/metadata/{genesis.json,genesis.ssz,config.yaml}`. Hand each operator only its own `data/validators/<operator>` keystores, over a private channel. Never distribute the mnemonic.
4. Each operator starts its node with `OPERATOR=<name> FEE_RECIPIENT=<own address> ./up.sh <genesis dir>`. `up.sh` generates the JWT locally.

## Multi-host P2P

The compose file is single-host: discovery is off on both layers, the original `nethermind` service runs with its peer manager off (`NETHERMIND_INITCONFIG_PEERMANAGERENABLED: "false"`), and only RPC and API ports are published, on 127.0.0.1. Peering across hosts needs an override and static peers. Not validated yet; try it on two hosts before the restart.

```yaml
# multi-host.override.yml, added to COMPOSE_FILE
services:
  nethermind:
    environment:
      NETHERMIND_INITCONFIG_PEERMANAGERENABLED: "true"
    ports: ["30303:30303/tcp", "30303:30303/udp"]
  beacon:
    ports: ["9000:9000/tcp", "9000:9000/udp"]
    command: !override  # the beacon command of docker-compose.yml, plus:
      # --libp2p-addresses=/ip4/<peer ip>/tcp/9000/p2p/<peer id>,...  --enr-address=<own public ip>
```

- Open TCP and UDP 30303 (devp2p) and 9000 (libp2p) between hosts. Keep 8551 (Engine API) and the RPC and API ports off the public interface.
- EL: collect each node's enode (`admin_nodeInfo`, or the `enode://` line of its log with the host's public IP in place of the logged one) and write the other nodes' enodes into `<genesis dir>/nethermind/static-nodes.json`, the file `--Init.StaticNodesPath` reads.
- CL: collect each beacon node's peer ID (`/eth/v1/node/identity`) and give the others as a comma-separated `--libp2p-addresses` list.
- Start one node first, publish its enode and multiaddr, then start the rest.

## Health checks

```bash
cd tools/daisugi-devnet
EL_RPC_PORT=<rpc> CL_HTTP_PORT=<api> EL_CONTAINER=<el> BN_CONTAINER=<bn> VC_CONTAINER=<vc> ./check.sh <genesis dir>
curl -s localhost:<api>/eth/v1/node/peer_count                    # connected > 0
curl -s localhost:<api>/eth/v1/beacon/states/head/finality_checkpoints
docker run --rm --entrypoint sh <nethermind image> -c 'sha256sum /nethermind/libnethermind_lean.so'
```

- `check.sh` fails on genesis mismatch, a failed `InitializeLeanBackend`, missing finality past the fork, a wrong fork boundary, or missed slots after the fork (`MISSED_SLOTS_MAX`, default 0).
- Proof path: `FX=<signer> PQ_KEY=<funded key> CHAIN_ID=<id> PORT=<rpc> OUT=<fresh dir> ./pq.sh` sends one series and checks inclusion and rejections.
- Sync: `EL_RPC_PORTS="<ref> <others>" CL_HTTP_PORTS="<ref> <others>" ./sync-check.sh <proof block>...` compares nodes block by block.

## Rollback and troubleshooting

- `InitializeLeanBackend ... failed` at startup: wrong native library or ABI. Rebuild from the pinned commit; check the library hash.
- Proofs rejected network-wide (`a wrapper dependency proof failed verification` for valid wrappers): a signer or node on another package version. All signers use the `FX` of 0.21.0.110; wallets use the `eip8288-lean` profile of AA-PQ#12.
- A node stuck behind: check peers first, then compare it with `sync-check.sh`. Wipe its data directory and resync from genesis over P2P.
- Restart from scratch: stop all nodes, generate a new genesis with a new timestamp, redistribute. There is no in-place downgrade: the chain is not valid for the previous ABI 12 stack.

## Remaining external actions

- Merge NethermindEth/nethermind#15955 into `feature/eip8288-lean`. A squash merge changes the commit hash, so re-pin to the merged hash or keep building from `fe94447c77`.
- lean-bindings: merge #9 into `main`, #21 into the #19 branch, then #19 into `main` (Rubo or Marc). Until then `0.21.0.110` comes from the validated `release/daisugi-f128` branch.
- asanso/AA-PQ: merge #11, #12, #10, then #13 (repository owner).
- Publish the images to a registry and record their digests, if operators pull rather than build.
