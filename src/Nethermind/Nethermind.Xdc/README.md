# Nethermind.Xdc

Nethermind's implementation of the [XDC Network](https://xdc.org) — the XDPoS 2.0 consensus engine plus the
execution, networking, storage and RPC behaviour that XDC layers on top of a standard Ethereum client.

Everything ships as two Autofac-based consensus plugins, selected by `sealEngineType` in the chainspec:

| Plugin                                            | `sealEngineType` | Module                                       | Chains                                                        |
| ------------------------------------------------- | ---------------- | -------------------------------------------- | ------------------------------------------------------------- |
| [`XdcPlugin`](XdcPlugin.cs)                        | `XDPoS`          | [`XdcModule`](XdcModule.cs)                   | XDC mainnet (`xdc.json`), Apothem testnet (`xdc-testnet.json`) |
| [`XdcSubnetPlugin`](XdcSubnetPlugin.cs)            | `XDPoSSubnet`    | [`XdcSubnetModule`](XdcSubnetModule.cs)       | XDC subnets                                                    |

`XdcSubnetModule` derives from `XdcModule` and only overrides what differs, so the two share nearly the whole
stack (see [Subnets](#subnets)).

## Table of contents

1. [Module layout](#module-layout)
2. [Consensus — XDPoS 2.0](#consensus--xdpos-20)
3. [Epochs, snapshots and masternodes](#epochs-snapshots-and-masternodes)
4. [Penalties and rewards](#penalties-and-rewards)
5. [Block and header format](#block-and-header-format)
6. [Networking](#networking)
7. [Synchronisation](#synchronisation)
8. [Execution differences](#execution-differences)
9. [Storage](#storage)
10. [JSON-RPC](#json-rpc)
11. [Configuration](#configuration)
12. [Subnets](#subnets)
13. [Testing](#testing)
14. [References](#references)

---

## Module layout

```
Nethermind.Xdc/
├── Contracts/     System contract wrappers (masternode voting, minted record)
├── Discovery/     Discv4 overrides — XDC has no ENR request/response
├── Errors/        Consensus-specific exception types
├── P2P/           xdpos2 (eth/100) subprotocol: vote, timeout, syncInfo
├── RLP/           Header, certificate, vote, timeout and snapshot encoders
├── RPC/           XDPoS_* module and eth_* extensions
├── Spec/          Chainspec engine parameters, release spec, spec provider
├── TxPool/        Special-transaction filters, gossip policy, comparer
├── Types/         Consensus value types (QC, TC, Vote, Snapshot, …)
└── *.cs           Consensus orchestration, managers, validators, producers
```

The consensus core is roughly:

```
┌──────────────────────────────────────────────────────────────────────┐
│                        XDPoS 2.0 CONSENSUS                           │
│                                                                      │
│  XdcHotStuff  ── round driver: leader check, propose, vote, timeout   │
│      │                                                               │
│      ├── XdcBlockProducer ── XdcSealer ── XdcBlockSuggester           │
│      │                                                               │
│      ▼                                                               │
│  XdcConsensusContext ── CurrentRound, HighestQC, LockQC,              │
│      │                  HighestTC, HighestCommitBlock                 │
│      │                                                               │
│      ├── VotesManager                ── collect votes → build QC      │
│      ├── QuorumCertificateManager    ── verify/commit QC, 3-chain     │
│      ├── TimeoutCertificateManager   ── timeouts → TC, liveness       │
│      ├── SyncInfoManager             ── QC/TC catch-up between peers  │
│      ├── EpochSwitchManager          ── epoch boundaries, committees  │
│      ├── SnapshotManager             ── candidate sets at gap blocks  │
│      ├── MasternodesCalculator       ── candidates − penalties        │
│      └── PenaltyHandler              ── liveness/penalty accounting   │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│  XdcBlockTree │ XdcHeaderStore │ XdcBlockStore │ XdcSnapshots DB      │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Consensus — XDPoS 2.0

XDPoS 2.0 is a HotStuff-derived BFT protocol. Blocks carry a round number and a quorum certificate for their
parent; a block is finalised once three consecutive rounds are chained on top of it.

### Round lifecycle

[`XdcHotStuff`](XdcHotStuff.cs) implements `IBlockProducerRunner` and drives one task per round. A round is
(re)started by a new head block, by a round change, or at startup, and only while the node is synced or
bootstrapping a fresh chain.

```
SetNewRound(N)
   │
   ├─ Vote on current head (if it is a V2 block and we are a masternode)
   │     └─ VotesManager.VerifyVotingRules → CastVote → broadcast
   │
   └─ TryPropose
         ├─ Parent = block referenced by HighestQC (not necessarily head)
         ├─ Leader for round N == our signer address?
         ├─ State for that parent available (process the fork block if not)?
         ├─ Wait until parent.Timestamp + MinePeriod
         └─ BuildAndProposeBlock → XdcBlockProducer → XdcSealer → block tree
```

The node proposes at most once and votes at most once per round.

### Leader selection

Round-robin over the epoch's masternode set ([`XdcHotStuff.GetLeaderAddress`](XdcHotStuff.cs)):

```
leaderIndex = (round % EpochLength) % masternodes.Length
```

On an epoch-switch round the *next* epoch's masternodes are computed first. The same formula is re-checked in
[`XdcSealValidator.ValidateParams`](XdcSealValidator.cs), which rejects a block whose recovered author is not
the expected leader.

### Voting rules

[`VotesManager.VerifyVotingRules`](VotesManager.cs) admits a vote only when all hold:

1. no vote has been cast at a round ≥ `CurrentRound` (no double voting);
2. the block's round equals `CurrentRound`;
3. `LockQC` is unset, **or** the block's parent QC round is above `LockQC`'s round, **or** the block descends
   from the locked block (ancestry walk).

Votes received from peers are additionally dropped when they are too far from the head block or the current
round.

### Quorum certificates

```
Vote (BlockRoundInfo + GapNumber, signed)
   │  signature recovered against the gap snapshot's candidate set
   ▼
Vote pool, keyed by (round, block hash)
   │  count ≥ masternodes × CertificateThreshold
   │  signatures re-checked against the epoch's masternodes, deduplicated by signer
   ▼
QuorumCertificate { ProposedBlockInfo, Signature[], GapNumber }
   │
   ▼
QuorumCertificateManager.CommitCertificate
```

Votes may arrive before the block they refer to; they are buffered and drained once the block shows up. Vote
pools are pruned to a short window of recent rounds.

`CommitCertificate` ([`QuorumCertificateManager`](QuorumCertificateManager.cs)) then:

1. raises `HighestQC` if the QC's round is higher;
2. raises `LockQC` to the proposed block's *parent* QC;
3. applies the 3-chain rule and, on success, updates `HighestCommitBlock` and calls
   `IBlockTree.ForkChoiceUpdated`;
4. advances the round to `qc.Round + 1`.

It is also invoked for every processed header as the main chain advances, which is how a syncing node rebuilds
consensus state from the chain alone.

Certificate verification requires `ceil(masternodes × CertificateThreshold)` distinct valid signatures over the
vote hash, and that the QC's `GapNumber` matches the gap block implied by the epoch switch.

### 3-chain finalisation

```
Round N-2          Round N-1          Round N
┌────────┐        ┌────────┐        ┌────────┐
│Block B │◀───────│Block C │◀───────│Block D │
│ QC(A)  │        │ QC(B)  │        │ QC(C)  │
└────────┘        └────────┘        └────────┘
     ▲
     └── committed when QC(D) is processed
```

The rounds must be strictly consecutive — a gap anywhere in the three-block chain blocks the commit — and the
committed head never moves backwards.

### Timeouts and liveness

[`TimeoutTimer`](TimeoutTimer.cs) fires after `TimeoutPeriod` seconds without progress.
[`TimeoutCertificateManager`](TimeoutCertificateManager.cs) then:

```
OnCountdownTimer
   ├─ masternode? sign Timeout(round, gapNumber) and broadcast
   ├─ TimeoutCounter++
   ├─ every TimeoutSyncThreshold timeouts → broadcast SyncInfo(HighestQC, HighestTC)
   └─ reset timer

HandleTimeoutVote (own or received)
   ├─ same round only, signer must be in the gap snapshot's candidate set
   ├─ pool by round; count ≥ masternodes × CertificateThreshold
   └─ TimeoutCertificate { Round, Signature[], GapNumber }
         └─ ProcessTimeoutCertificate → HighestTC, SetNewRound(round + 1)
```

Timeouts referring to an epoch far from the head are discarded.

### SyncInfo

[`SyncInfoManager`](SyncInfoManager.cs) exchanges `(HighestQC, HighestTC)` so a lagging node can jump to the
current round without waiting for blocks. Incoming `SyncInfo` is rejected when both certificates are at or
below what the node already knows, or when either certificate fails verification; otherwise the TC is processed
and the QC committed.

### Fork choice

[`XdcBlockTree`](XdcBlockTree.cs) replaces total-difficulty fork choice, since every XDC block adds difficulty 1
([`XdcDifficultyCalculator`](XdcDifficultyCalculator.cs)):

- A suggested block is accepted only if it descends from `HighestCommitBlock`; anything on a dead fork, or at or
  below the committed height, is rejected.
- Equal-TD ties are broken by consensus round, then by whether the node produced the block itself.

### Forensics

[`IForensicsProcessor`](IForensicsProcessor.cs) receives committed QC chains and vote-equivocation candidates.
`NullForensicsProcessor` is registered by default; equivocation detection and proof gossip are not yet wired up.

---

## Epochs, snapshots and masternodes

An epoch is `EpochLength` blocks. The candidate set for an epoch is snapshotted `Gap` blocks *before* the epoch
starts, so validators can be agreed on ahead of the switch.

```
Block number                    Action
──────────────────────────────────────────────────────────────────────
epochBase                       Epoch switch: header carries Validators + Penalties
…                               Normal blocks
epochBase + (EpochLength − Gap) Gap block: snapshot candidates from the voting contract
…                               Normal blocks
epochBase + EpochLength         Next epoch switch, using the snapshot above
```

With `EpochLength = 900` and `Gap = 450` the gap block sits halfway through the epoch, 450 blocks before the
epoch it configures. A snapshot is `{ BlockNumber, HeaderHash, NextEpochCandidates }`, RLP-encoded into the
`XdcSnapshots` database and cached in memory. [`SnapshotManager`](SnapshotManager.cs) reads candidates from the
masternode voting contract ordered by stake, or from `GenesisMasterNodes` at genesis, and can recompute a
missing snapshot on demand as long as the gap block's state is still available.

Epoch-switch detection ([`EpochSwitchManager`](EpochSwitchManager.cs)) differs either side of `SwitchBlock`:
below it, a switch is every `EpochLength`th block; above it, a block starts a new epoch when its parent QC's
round falls in the previous epoch. [`BaseEpochSwitchManager`](BaseEpochSwitchManager.cs) resolves and caches the
`EpochSwitchInfo` (masternodes, standby nodes, penalties) for any header by walking back to its epoch-switch
block.

[`MasternodesCalculator`](MasternodesCalculator.cs) derives the next committee:

```
candidates = snapshot(gap).NextEpochCandidates
penalties  = PenaltyHandler.HandlePenalties(...)
masternodes = (candidates − penalties).Take(MaxMasternodes)
```

The first V2 epoch is the exception: it uses the raw candidate list, since there is no V2 history to penalise
against yet.

Under `TIPUpgradeReward`, candidates beyond the masternode cap are further split into **protector** and
**observer** tiers (`MaxProtectorNodes`, `MaxObserverNodes`) for reward purposes.

---

## Penalties and rewards

### Penalties

[`PenaltyHandler`](PenaltyHandler.cs) counts blocks produced per miner since the previous epoch switch and
penalises any masternode that produced too few (or none at all). The threshold is a hard-coded single block
until `TIPUpgradePenalty` activates, after which the configured `MinimumMinerBlockPerEpoch` applies. Two
comeback paths exist, selected by the same flag:

| | Pre-`TIPUpgradePenalty` | Post-`TIPUpgradePenalty` |
| --- | --- | --- |
| Penalty window | `XdcConstants.LimitPenaltyEpochV2` epochs | `LimitPenaltyEpoch` epochs |
| Comeback scan | last `RangeReturnSigner` blocks | last `EpochLength` blocks |
| Comeback condition | one signing tx for a block at a `MergeSignRange` multiple | `MinimumSigningTx` such signing txs |

Penalised addresses are written into the epoch-switch header's `Penalties` field and re-derived independently
by every validator during seal validation.

### Rewards

[`XdcRewardCalculator`](XdcRewardCalculator.cs) pays out **only at epoch-switch blocks**, based on signing
transactions observed two epochs back (blocks at heights that are multiples of `MergeSignRange`):

- **Pre-`TIPUpgradeReward`** — `Reward` XDC for the epoch, split proportionally to each masternode's signing
  count.
- **Post-`TIPUpgradeReward`** — fixed `MasternodeReward` / `ProtectorReward` / `ObserverReward` (XDC) per
  qualifying signer, with minted and burned totals reported to the minted-record contract
  ([`IMintedRecordContract`](Contracts/IMintedRecordContract.cs)).

Each signer's reward is split 90% to the candidate owner (resolved through the masternode voting contract) and
10% to `FoundationWalletAddr`.

The per-epoch breakdown is persisted by [`RewardsStore`](RewardsStore.cs) in the `XdcRewards` database, which
backs `eth_getRewardByHash` and `XDPoS_getRewardByAccount`.

### Signing transactions

[`SignTransactionManager`](SignTransactionManager.cs) is what makes the above possible. On every processed head
block whose number is a multiple of `MergeSignRange`, a masternode submits a zero-gas-price transaction to
`BlockSignerContract` carrying `sign(uint256 blockNumber, bytes32 blockHash)`. Only recent blocks are signed, so
a node catching up does not flood the pool replaying old ones.

---

## Block and header format

[`XdcBlockHeader`](XdcBlockHeader.cs) extends `BlockHeader` with:

| Field | Meaning |
| --- | --- |
| `Validators` | Concatenated masternode addresses; set only on epoch-switch blocks |
| `Penalties` | Concatenated penalised addresses; set only on epoch-switch blocks |
| `Validator` | 65-byte ECDSA seal over the header |
| `ExtraConsensusData` | Decoded from `ExtraData` |
| `IsSelfMined` | Local flag used as the last fork-choice tie-break |

`ExtraData` for a V2 block is a consensus-version byte followed by RLP-encoded
[`ExtraFieldsV2`](Types/ExtraFieldsV2.cs) — `{ BlockRound, QuorumCert }`. Headers below `SwitchBlock` keep the
V1 clique-style layout: 32-byte vanity, packed signer list, 65-byte seal.

Other header invariants: uncles must be empty ([`MustBeEmptyUnclesValidator`](MustBeEmptyUnclesValidator.cs)),
difficulty is always 1, `MixHash` is zero, and on epoch-switch blocks the vote nonce must be zero.

Consensus value types live in [`Types/`](Types):

```csharp
BlockRoundInfo      { Hash, Round, BlockNumber }
Vote                { ProposedBlockInfo, GapNumber, Signature, Signer }
QuorumCertificate   { ProposedBlockInfo, Signature[], GapNumber }
Timeout             { Round, Signature, GapNumber, Signer }
TimeoutCertificate  { Round, Signature[], GapNumber }
SyncInfo            { HighestQuorumCert, HighestTimeoutCert }
Snapshot            { BlockNumber, HeaderHash, NextEpochCandidates }
EpochSwitchInfo     { Masternodes, StandbyNodes, Penalties, EpochSwitchBlockInfo, EpochSwitchParentBlockInfo }
```

Votes and timeouts are signed over the Keccak hash of their RLP encoding, and all signatures must be low-S.

---

## Networking

### xdpos2 subprotocol

[`XdcProtocolHandler`](P2P/XdcProtocolHandler.cs) extends `Eth63ProtocolHandler` and advertises itself as
`xdpos2`, protocol version **100**, adding three message codes:

| Code | Message | Handler |
| --- | --- | --- |
| `0xe0` | `VoteMsg` | `VotesManager.OnReceiveVote` |
| `0xe1` | `TimeoutMsg` | `TimeoutCertificateManager.OnReceiveTimeout` |
| `0xe2` | `SyncInfoMsg` | `SyncInfoManager.VerifySyncInfo` → `ProcessSyncInfo` |

Votes and timeouts are ignored entirely while the node is syncing (unless it is bootstrapping from genesis), and
re-broadcast is deduplicated so a message is forwarded to a peer at most once.

[`XdcP2PCapabilityResolver`](XdcP2PCapabilityResolver.cs) replaces the default resolver so the node advertises
exactly `eth/100`, `eth/164` and `eth/165` — one per registered handler. A peer that offers nothing from that set
is disconnected as having no capability in common.

### Discovery

[`XdcDiscoveryApp`](Discovery/XdcDiscoveryApp.cs) overrides the default Discv4 app: XDC does not implement the
ENR request/response messages, so [`XdcKademliaAdapter`](Discovery/XdcKademliaAdapter.cs) disables remote ENR
refresh and [`XdcNettyDiscoveryHandler`](Discovery/XdcNettyDiscoveryHandler.cs) plus
[`XdcPingMsgSerializer`](Discovery/XdcPingMsgSerializer.cs) adjust wire compatibility.

---

## Synchronisation

Header validation needs the gap-block snapshot for the epoch being validated, but fast sync never processes
those blocks. XDC therefore rebuilds them as part of state sync:

- [`XdcStateSyncSnapshotManager`](XdcStateSyncSnapshotManager.cs) computes every gap block between the first
  reachable epoch switch and the pivot.
- [`XdcStateSyncPivot`](XdcStateSyncPivot.cs) walks those gap blocks as intermediate sync targets, storing each
  snapshot as its state becomes available, before finalising on the real pivot.
- [`XdcStateSyncDownloader`](P2P/XdcStateSyncDownloader.cs) and
  [`XdcStateSyncAllocationStrategyFactory`](XdcStateSyncAllocationStrategyFactory.cs) adapt peer allocation to
  that multi-target flow.
- [`XdcBeaconSyncStrategy`](XdcBeaconSyncStrategy.cs) neutralises the merge/beacon code paths and reports the
  configured pivot as the sync target.

---

## Execution differences

### Special transactions

[`XdcExtensions.Transactions`](XdcExtensions.Transactions.cs) recognises two **overlapping but distinct** sets of
transactions by recipient, and [`XdcTransactionProcessor`](XdcTransactionProcessor.cs) dispatches on them
separately:

| Set | Recipients | Effect |
| --- | --- | --- |
| Fee-exempt | `BlockSignerContract`, `RandomizeSMCBinary` | Sender buys no gas and pays no fee |
| Execution-skipping | `BlockSignerContract`, plus the DEX/lending contracts (`XDCXAddressBinary`, `XDCXLendingAddressBinary`, `XDCXLendingFinalizedTradeAddressBinary`, `TradingStateAddressBinary`) | No intrinsic gas, no gas validation, EVM skipped, empty successful receipt |

The overlap is only partial, and the difference is what determines gas accounting:

- **Sign** transactions are in both sets — free *and* skipped — but the nonce is still checked and incremented.
- **Randomize** transactions are fee-exempt only. They charge intrinsic gas, take the normal nonce path and
  **execute in the EVM**, consuming block gas; only the sender's payment is waived.
- **Trading / lending** transactions are execution-skipping only, with nonce checks bypassed.

The DEX/lending contracts qualify only inside the `TipXDCX` → `TIPXDCXReceiverDisable` window, which is already
closed on mainnet.

### Fees and blacklist

- Post-`TipTrc21Fee`, gas fees are paid to the **candidate owner** of the block beneficiary rather than the
  beneficiary itself.
- Post-`BlackListHFNumber`, transactions with a blacklisted sender or recipient are rejected during execution,
  and on pool admission, so they are never gossiped.

### Block execution context

[`XdcBlockProcessor`](XdcBlockProcessor.cs) derives `PrevRandao` from the block number rather than a beacon
value, and forces blob base fee to zero, since XDC enables the `BLOBBASEFEE` opcode without blob transactions.

### Gas limit and base fee

- [`XdcGasLimitCalculator`](XdcGasLimitCalculator.cs) uses a fixed target (`TargetBlockGasLimit`, default
  420,000,000) until `DynamicGasLimitBlock`, after which the standard target-adjusted calculator applies.
- [`XdcBaseFeeCalculator`](XdcBaseFeeCalculator.cs) returns a constant **12.5 gwei** base fee when EIP-1559 is
  enabled, mirroring the reference client.

### Transaction pool

- [`SignTransactionFilter`](TxPool/SignTransactionFilter.cs) accepts fee-exempt transactions only from current
  epoch candidates, and only when the signed block is recent.
- [`BlackListedAddressFilter`](TxPool/BlackListedAddressFilter.cs) rejects transactions with a blacklisted
  sender or recipient once `BlackListHFNumber` activates, so they never reach a block or a peer. The rejection
  code is deliberately not `AcceptTxResult.Invalid`, which would disconnect the relaying peer.
- [`XdcTxGossipPolicy`](TxPool/XdcTxGossipPolicy.cs) withholds the DEX/lending family; sign and randomize
  transactions are gossiped normally.
- [`XdcTxFilterPipeline`](TxPool/XdcTxFilterPipeline.cs) lets the fee-exempt transactions bypass the
  block-producer filters, since they legitimately carry a zero gas price. The DEX/lending family still goes
  through them.
- [`XdcTransactionComparerProvider`](TxPool/XdcTransactionComparerProvider.cs) orders the pool with XDC's
  fee semantics.

---

## Storage

Both are registered by [`XdcModule`](XdcModule.cs):

| Database | Contents | Written by |
| --- | --- | --- |
| `XdcSnapshots` | RLP-encoded candidate snapshots, keyed by gap block hash | [`SnapshotManager`](SnapshotManager.cs) |
| `XdcRewards` | JSON epoch-reward breakdowns, keyed by epoch block hash | [`RewardsStore`](RewardsStore.cs) |

Neither has dedicated RocksDB tuning options, which is why
[`XdcRocksDbConfigFactory`](XdcRocksDbConfigFactory.cs) exists.

[`XdcHeaderStore`](XdcHeaderStore.cs), [`XdcBlockStore`](XdcBlockStore.cs) and
[`XdcBlockhashStore`](XdcBlockhashStore.cs) exist so that headers round-trip through the XDC RLP decoders
registered by [`XdcHeaderModule`](XdcHeaderModule.cs).

---

## JSON-RPC

### `Xdc` module — [`IXdcRpcModule`](RPC/IXdcRpcModule.cs)

The methods are named `XDPoS_*`, but the module is registered as `Xdc` — that is the name to put in
`JsonRpc.EnabledModules`.

| Method | Purpose |
| --- | --- |
| `XDPoS_getSnapshot(block)` | Candidate snapshot at a block number |
| `XDPoS_getSnapshotAtHash(hash)` | Candidate snapshot at a block hash |
| `XDPoS_getSigners(block)` | Authorised signers at a block number |
| `XDPoS_getSignersAtHash(hash)` | Authorised signers at a block hash |
| `XDPoS_getMasternodesByNumber(block)` | Masternodes, standby nodes and penalties at a block |
| `XDPoS_getLatestPoolStatus()` | Current vote pool and timeout pool, keyed by pool key |
| `XDPoS_getV2BlockByNumber(block)` | V2 block info: round, QC, committed status |
| `XDPoS_getV2BlockByHash(hash)` | Same, by hash |
| `XDPoS_networkInformation()` | Network id, system contract addresses and the effective XDPoS config |
| `XDPoS_getMissedRoundsInEpochByBlockNum(block)` | Rounds in the epoch with no block (V2 only) |
| `XDPoS_getRewardByAccount(account, begin, end)` | Rewards paid to an account across a block range |
| `XDPoS_getEpochNumbersBetween(begin, end)` | Epoch numbers spanned by a block range |
| `XDPoS_getBlockInfoByV2EpochNum(epoch)` | Epoch-switch block for a V2 epoch |
| `XDPoS_calculateBlockInfoByV1EpochNum(epoch)` | Epoch-switch block for a V1 epoch |
| `XDPoS_getBlockInfoByEpochNum(epoch)` | Epoch-switch block, V1 or V2 |

### `eth` extensions — [`IXdcExtendedEthRpcModule`](RPC/IXdcExtendedEthRpcModule.cs)

| Method | Purpose |
| --- | --- |
| `eth_getOwnerByCoinbase(coinbase, block)` | Masternode owner for a coinbase address |
| `eth_getRewardByHash(blockHash)` | Epoch reward breakdown for an epoch-switch block |
| `eth_getTransactionAndReceiptProof(txHash)` | Merkle proofs for a transaction and its receipt |

---

## Configuration

All XDC parameters live under `engine.XDPoS.params` in the chainspec and are bound to
[`XdcChainSpecEngineParameters`](Spec/XdcChainSpecEngineParameters.cs), then projected onto
[`XdcReleaseSpec`](Spec/XdcReleaseSpec.cs) by
[`XdcChainSpecBasedSpecProvider`](Spec/XdcChainSpecBasedSpecProvider.cs).

Note that XDC has **two** dimensions of configuration: standard block-number forks (`TipTrc21Fee`,
`TIPUpgradeReward`, …) and *round*-based V2 configs. Both are resolved together, so the effective spec depends
on the block number *and* the consensus round.

### Chain-level parameters

| Parameter | Unit | Description |
| --- | --- | --- |
| `epoch` | blocks | Epoch length; also the modulus for leader rotation. `900` on mainnet and Apothem |
| `gap` | blocks | Distance before an epoch start at which the candidate snapshot is taken. `450` |
| `period` | seconds | Nominal block period |
| `switchBlock` | block | First V2 block; below it, V1 (clique-style) rules apply |
| `switchEpoch` | epoch | Epoch number corresponding to `switchBlock`, used to number V2 epochs |
| `reward` | XDC | Per-epoch reward pool used before `TIPUpgradeReward` |
| `foundationWalletAddr` | address | Receives 10% of every signer reward |
| `masternodeVotingContract` | address | Candidate list, stake and owner lookups |
| `blockSignerContract` | address | Target of signing transactions |
| `randomizeSMCBinary` | address | Randomize contract; fee-exempt |
| `XDCXAddressBinary`, `XDCXLendingAddressBinary`, `XDCXLendingFinalizedTradeAddressBinary`, `tradingStateAddressBinary` | address | DEX/lending contracts with special transaction handling |
| `MergeSignRange` | blocks | Only blocks at multiples of this height are signed and counted for rewards. `15` |
| `RangeReturnSigner` | blocks | Comeback scan window before `TIPUpgradePenalty`. `150` |
| `genesisMasternodes` | address[] | Initial committee; parsed from genesis `extraData` when `switchBlock == 0` |
| `blackListedAddresses` | address[] | Blocked senders/recipients once `BlackListHFNumber` activates |

### Fork activation blocks

| Parameter | Effect once activated |
| --- | --- |
| `tip2019Block` | TIP-2019 rules |
| `TipTrc21Fee` | Gas fees paid to the masternode owner instead of the beneficiary |
| `TipXDCX` | DEX/lending transactions get special handling |
| `TIPXDCXMinerDisable` / `TIPXDCXReceiverDisable` | End of the miner-side / receiver-side XDCX handling |
| `BlackListHFNumber` | Blacklist enforcement |
| `TIPUpgradeReward` | Fixed-rate masternode/protector/observer rewards and minted-record accounting |
| `TIPUpgradePenalty` | New penalty comeback rules (`LimitPenaltyEpoch`, `MinimumSigningTx`) |
| `DynamicGasLimitBlock` | Target-adjusted gas limit instead of the fixed target |

### `v2Configs` — round-scoped parameters

`v2Configs` is a list ordered by `SwitchRound`; the entry with the greatest `SwitchRound ≤ round` applies. The
list **must** contain an entry with `SwitchRound: 0` and must not repeat a round — a chainspec that violates
either fails to load.

| Field | Unit | Description |
| --- | --- | --- |
| `SwitchRound` | round | Round from which this entry applies |
| `MaxMasternodes` | count | Committee cap. `108` on mainnet |
| `MaxProtectorNodes` / `MaxObserverNodes` | count | Reward-tier caps (post-`TIPUpgradeReward`) |
| `CertificateThreshold` | fraction | Share of masternodes required for a QC or TC. `0.667` on mainnet |
| `TimeoutPeriod` | **seconds** | Round timeout before a timeout vote is broadcast |
| `TimeoutSyncThreshold` | count | Broadcast `SyncInfo` after this many consecutive timeouts |
| `MinePeriod` | **seconds** | Minimum spacing between a parent block and its child. `2` |
| `MasternodeReward` / `ProtectorReward` / `ObserverReward` | XDC | Fixed per-signer epoch rewards (post-`TIPUpgradeReward`). Stated in XDC, as in the reference client, and scaled to wei on load. `63.42` on Apothem |
| `MinimumMinerBlockPerEpoch` | blocks | Below this, a masternode is penalised. Only honoured once `TIPUpgradePenalty` is active; before that a hard-coded `1` applies |
| `LimitPenaltyEpoch` | epochs | Penalty duration used post-`TIPUpgradePenalty`. `5` on Apothem; a chainspec that omits it falls back to `1` |
| `MinimumSigningTx` | count | Signing transactions needed to leave penalty |

Example — mainnet's current entry:

```json
{
  "SwitchRound": 3200000,
  "MaxMasternodes": 108,
  "CertificateThreshold": 0.667,
  "TimeoutSyncThreshold": 3,
  "TimeoutPeriod": 10,
  "MinePeriod": 2
}
```

---

## Subnets

[`XdcSubnetModule`](XdcSubnetModule.cs) reuses the whole mainnet stack and overrides only:

| Component | Subnet replacement | Why |
| --- | --- | --- |
| Engine parameters | [`XdcSubnetChainSpecEngineParameters`](Spec/XdcSubnetChainSpecEngineParameters.cs) | `sealEngineType` is `XDPoSSubnet` |
| Header decoding | [`XdcSubnetHeaderDecoder`](RLP/XdcSubnetHeaderDecoder.cs) via [`XdcSubnetBlockHeader`](XdcSubnetBlockHeader.cs) | Different header layout |
| Block production | [`XdcSubnetBlockProducer`](XdcSubnetBlockProducer.cs) | Subnet header fields |
| Epoch switching | [`SubnetEpochSwitchManager`](SubnetEpochSwitchManager.cs) | Epoch numbering without the V1 era |
| Snapshots | [`SubnetSnapshotManager`](SubnetSnapshotManager.cs) / [`SubnetSnapshot`](Types/SubnetSnapshot.cs) | Snapshots also carry `NextEpochPenalties` |
| Masternodes | [`SubnetMasternodesCalculator`](SubnetMasternodesCalculator.cs) | Penalties come from the snapshot, not recomputed |
| Penalties | [`SubnetPenaltyHandler`](SubnetPenaltyHandler.cs) | Penalties are supplied by the snapshot |
| Seal validation | [`XdcSubnetSealValidator`](XdcSubnetSealValidator.cs) | Subnet header/seal shape |
| Rewards | [`XdcSubnetRewardCalculator`](XdcSubnetRewardCalculator.cs) | Subnet reward model |
| Chainspec loading | [`XdcSubnetChainSpecLoader`](XdcSubnetChainSpecLoader.cs) | Builds a subnet genesis header |

---

## Testing

Tests live in `Nethermind.Xdc.Test`. Run the whole suite or a single test with:

```bash
dotnet test --project src/Nethermind/Nethermind.Xdc.Test/Nethermind.Xdc.Test.csproj -c release
```

Areas worth covering when changing this module:

- **Consensus logic** — leader selection at epoch boundaries, voting-rule rejection cases, QC/TC threshold
  arithmetic, 3-chain commit and its round-continuity guards.
- **Fork choice** — dead-fork rejection against the committed block, equal-TD tie-breaks.
- **Epoch machinery** — snapshot gap arithmetic, epoch lookups across the V1/V2 boundary, snapshot recovery
  during state sync.
- **Penalties and rewards** — both sides of `TIPUpgradePenalty` / `TIPUpgradeReward`, signing-transaction
  counting across `MergeSignRange`.
- **Execution** — special-transaction gas and nonce handling, TRC21 fee redirection, blacklist enforcement.
- **Serialization** — header, certificate, vote, timeout and snapshot RLP round-trips against reference
  vectors.

---

## References

- XDPoS 2.0 — [XDPoSChain reference client](https://github.com/XinFinOrg/XDPoSChain)
- HotStuff: BFT Consensus with Linearity and Responsiveness — Yin et al., PODC '19
- Practical Byzantine Fault Tolerance — Castro & Liskov, OSDI '99
