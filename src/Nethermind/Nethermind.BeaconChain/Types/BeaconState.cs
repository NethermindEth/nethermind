// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Electra <c>BeaconState</c> (mainnet preset limits).</summary>
[SszContainer]
public partial class BeaconStateElectra
{
    public ulong GenesisTime { get; set; }

    public Hash256? GenesisValidatorsRoot { get; set; }

    public ulong Slot { get; set; }

    public Fork? Fork { get; set; }

    public BeaconBlockHeader? LatestBlockHeader { get; set; }

    [SszVector(8192)]
    public Hash256[]? BlockRoots { get; set; }

    [SszVector(8192)]
    public Hash256[]? StateRoots { get; set; }

    [SszList(16_777_216)]
    public Hash256[]? HistoricalRoots { get; set; }

    public Eth1Data? Eth1Data { get; set; }

    [SszList(2048)]
    public Eth1Data[]? Eth1DataVotes { get; set; }

    public ulong Eth1DepositIndex { get; set; }

    [SszList(1_099_511_627_776)]
    public Validator[]? Validators { get; set; }

    [SszList(1_099_511_627_776)]
    public ulong[]? Balances { get; set; }

    [SszVector(65_536)]
    public Hash256[]? RandaoMixes { get; set; }

    [SszVector(8192)]
    public ulong[]? Slashings { get; set; }

    [SszList(1_099_511_627_776)]
    public byte[]? PreviousEpochParticipation { get; set; }

    [SszList(1_099_511_627_776)]
    public byte[]? CurrentEpochParticipation { get; set; }

    [SszVector(4)]
    public BitArray? JustificationBits { get; set; }

    public Checkpoint? PreviousJustifiedCheckpoint { get; set; }

    public Checkpoint? CurrentJustifiedCheckpoint { get; set; }

    public Checkpoint? FinalizedCheckpoint { get; set; }

    [SszList(1_099_511_627_776)]
    public ulong[]? InactivityScores { get; set; }

    public SyncCommittee? CurrentSyncCommittee { get; set; }

    public SyncCommittee? NextSyncCommittee { get; set; }

    public ExecutionPayloadHeader? LatestExecutionPayloadHeader { get; set; }

    public ulong NextWithdrawalIndex { get; set; }

    public ulong NextWithdrawalValidatorIndex { get; set; }

    [SszList(16_777_216)]
    public HistoricalSummary[]? HistoricalSummaries { get; set; }

    public ulong DepositRequestsStartIndex { get; set; }

    public ulong DepositBalanceToConsume { get; set; }

    public ulong ExitBalanceToConsume { get; set; }

    public ulong EarliestExitEpoch { get; set; }

    public ulong ConsolidationBalanceToConsume { get; set; }

    public ulong EarliestConsolidationEpoch { get; set; }

    [SszList(134_217_728)]
    public PendingDeposit[]? PendingDeposits { get; set; }

    [SszList(134_217_728)]
    public PendingPartialWithdrawal[]? PendingPartialWithdrawals { get; set; }

    [SszList(262_144)]
    public PendingConsolidation[]? PendingConsolidations { get; set; }
}

/// <summary>Fulu <c>BeaconState</c>: Electra plus the EIP-7917 <c>proposer_lookahead</c> vector.</summary>
[SszContainer]
public partial class BeaconStateFulu : BeaconStateElectra
{
    /// <remarks>Length is <c>(MIN_SEED_LOOKAHEAD + 1) * SLOTS_PER_EPOCH</c>.</remarks>
    [SszVector(64)]
    public ulong[]? ProposerLookahead { get; set; }
}
