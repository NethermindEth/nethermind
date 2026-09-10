# Nethermind.Consensus.Qbft

Byte-compatible port of Hyperledger Besu's QBFT consensus (the `istanbul/100` sub-protocol) so that
Nethermind can run as an observer or validator on Besu QBFT networks, and as a validator on
networks that were migrated from IBFT 2.0 to QBFT.

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

## Public network

`Chains/rbb.json` and `configs/rbb.json` describe Rede Blockchain Brasil (chain id 12120014), a
public-permissioned Besu QBFT network. Run it with `nethermind --config rbb`.
