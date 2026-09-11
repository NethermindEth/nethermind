# Nethermind.Consensus.Qbft

Byte-compatible port of Hyperledger Besu's QBFT consensus (the `istanbul/100` sub-protocol) so that
Nethermind can run as an observer or validator on Besu QBFT networks, and as a validator on
networks that were migrated from IBFT 2.0 to QBFT. It can also follow a chain still running
IBFT 2.0.

## What is covered

- Wire protocol `istanbul/100`, message space `0x16`, messages `PROPOSAL` (0x12), `PREPARE` (0x13),
  `COMMIT` (0x14), `ROUND_CHANGE` (0x15); both the current shape (block access list slot, Besu 26.1+)
  and the legacy three-item shape.
- QBFT extra-data codec and the IBFT 2.0 codec used for blocks below `startblock` after a migration.
  Block hashes follow Besu: the on-chain hash strips round and commit seals, the commit-seal digest
  keeps the round.
- Validator selection by block header (voting, epoch reset, `n/2+1` majority) and by contract
  (`getValidators()` at the parent state), per-fork `transitions` with validator overrides.
- Round timer back-off (`requesttimeoutseconds * 2^round`), block period, empty block period,
  test-only millisecond block period, optional early round change (`Qbft.EarlyRoundChange`).
- Block reward and mining beneficiary per fork, per-transaction gas limit (`pertxgaslimit`).
- JSON-RPC module `qbft`: Besu's `qbft_discardValidatorVote`, `qbft_getPendingVotes`,
  `qbft_getSignerMetrics`, `qbft_getValidatorsByBlockHash`, `qbft_getValidatorsByBlockNumber`,
  `qbft_proposeValidatorVote`, `qbft_getRequestTimeoutSeconds`, plus `qbft_getBlockCommitters`,
  `qbft_getConfig` and `qbft_getNodeStatus` (Nethermind additions).

## Configuration

The engine is selected by the chainspec. Either format works:

- Parity-style chainspec with `"engine": { "qbft": { "params": { ... } } }` (see `Chains/rbb.json`);
- a Besu `genesis.json` used directly as `Init.ChainSpecPath` (the `config.qbft`, `config.transitions`,
  `config.contractSizeLimit` and `config.discovery.bootnodes` sections are understood).

Parameter names are Besu's (`blockperiodseconds`, `emptyblockperiodseconds`, `epochlength`,
`requesttimeoutseconds`, `validatorcontractaddress`, `miningbeneficiary`, `blockreward`,
`pertxgaslimit`, `startblock`, `transitions`, ...). Defaults match Besu.

A node is an observer unless `Mining.Enabled=true`; the validator key is the node key or the account
selected by `KeyStore.BlockAuthorAccount`. Consensus starts once the node is within a few blocks of
the head and pauses while it is syncing.

Runtime options (`Qbft` config section):

| Option | Default | Meaning |
| --- | --- | --- |
| `Qbft.EarlyRoundChange` | `false` | Move to a higher round when `f + 1` validators already did (Besu `--Xqbft-enable-early-round-change`). |
| `Qbft.LegacyMessageEncoding` | `false` | Emit the pre-Besu-26.1 proposal/round-change shape without the block access list slot. |

## IBFT 2.0

Besu's older BFT engine is supported as a separate, follow-only engine, selected by a `config.ibft2`
section in a Besu genesis. Header rules, hashing, validator voting and proposer rotation are shared
with QBFT; only the extra-data codec differs, and the IBFT 2.0 one is the stricter of the two. Taking
part in IBFT 2.0 consensus would additionally need Besu's `IBF/1` sub-protocol, which is not
implemented, so no block producer, sealer or protocol handler is registered for it. Its JSON-RPC
module is `ibft`, carrying the six methods Besu shares between `ibft_` and `qbft_`.

A chain migrated from IBFT 2.0 to QBFT keeps both sections and is a QBFT chain: the `qbft` section
carries `startblock` and the `ibft2` one describes only the era below it.

## Public networks

| Config | Chain | Chain id | Engine |
| --- | --- | --- | --- |
| `rbb` | Rede Blockchain Brasil | 12120014 | QBFT |
| `kalychain` | KalyChain | 3888 | QBFT |
| `alastria` | Alastria Red B | 2020 | IBFT 2.0 |
| `lacchain` | LACChain Mainnet Omega | 648541 | IBFT 2.0 |
| `lacchain-protestnet` | LACChain Open-ProTestnet | 648540 | IBFT 2.0 |

Run one with `nethermind --config <name>`. Each chainspec is the network's own published Besu
genesis with a `config.discovery.bootnodes` section added, and
`Nethermind.Consensus.Qbft.Test/Config/ShippedBftChainSpecTests.cs` pins every genesis hash to what
the live network reports.

KalyChain is the one exception: its chainspec carries a `transitions.qbft` entry at block 51,192,000
that the project has never published. From that block the chain pays 3 KLC rather than the scheduled
1464843750000000 wei, and pays it to `0x8b80800Cf6dA88D59EB09CaE4Fd2196423c48b26` instead of the
proposer. A node running the published genesis diverges on the state root of that block, which
carries no transactions, so only the reward can differ. The entry was read off the chain with
`trace_block`, which reports the reward author and value for any historical block and is the quickest
way to diagnose a reward divergence on a Besu BFT chain. Their published file is stale because the
project has moved to a successor chain, id 3890, while 3888 keeps producing blocks.

Fast sync and snap sync are off for all of them: Besu serves snap only when started with
`--snapsync-server-enabled`, and `GetNodeData` is gone from eth/67, so there is no state-download
path and the chains are synced in full.

All but KalyChain are permissioned to some degree. Alastria runs with discovery disabled and a node
allowlist, so peering needs the operator to enrol the node key; the bootnodes are dialled directly
rather than discovered. Both LACChain networks gate writers on-chain. KalyChain is open to join.

Two quirks of these genesis files are worth knowing, because Besu ignores both and so does this
loader: KalyChain schedules Paris through Prague by block number, which Besu has no schedule for (its
head is long past `pragueBlock` and still carries no withdrawals root), and Alastria's `transitions`
entry sets `requesttimeoutseconds`, which is not one of the eight keys Besu's `BftFork` reads. A
genesis key of that shape is logged at startup rather than silently dropped.
