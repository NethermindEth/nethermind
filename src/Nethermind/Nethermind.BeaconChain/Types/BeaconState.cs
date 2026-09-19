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

/// <summary>
/// Gloas <c>BeaconState</c> (specs/gloas/beacon-chain.md, "Modified containers", fetched from
/// ethereum/consensus-specs `master` 2026-09-19): <c>ProgressiveContainer</c>, <c>ACTIVE_FIELDS</c>
/// width 46, no gaps. Declared fresh rather than inheriting <see cref="BeaconStateFulu"/>: Gloas removes
/// <c>latest_execution_payload_header</c> (EIP-7732 moves the payload out of state entirely) and retypes
/// several inherited fields from bounded lists to <c>ProgressiveList</c> (EIP-7688), so this is not an
/// additive change C# inheritance could express — every field is restated in spec order and indexed with
/// <see cref="SszFieldAttribute"/>, which is what makes the SszGenerator treat this as a progressive
/// container (see the remark at the top of GloasContainers.cs).
/// </summary>
[SszContainer]
public partial class BeaconStateGloas
{
    [SszField(0)]
    public ulong GenesisTime { get; set; }

    [SszField(1)]
    public Hash256? GenesisValidatorsRoot { get; set; }

    [SszField(2)]
    public ulong Slot { get; set; }

    [SszField(3)]
    public Fork? Fork { get; set; }

    [SszField(4)]
    public BeaconBlockHeader? LatestBlockHeader { get; set; }

    [SszField(5)]
    [SszVector(8192)]
    public Hash256[]? BlockRoots { get; set; }

    [SszField(6)]
    [SszVector(8192)]
    public Hash256[]? StateRoots { get; set; }

    /// <remarks>Unmodified in Gloas: still a plain bounded list, not progressive.</remarks>
    [SszField(7)]
    [SszList(16_777_216)]
    public Hash256[]? HistoricalRoots { get; set; }

    [SszField(8)]
    public Eth1Data? Eth1Data { get; set; }

    /// <remarks>Unmodified in Gloas: still a plain bounded list, not progressive.</remarks>
    [SszField(9)]
    [SszList(2048)]
    public Eth1Data[]? Eth1DataVotes { get; set; }

    [SszField(10)]
    public ulong Eth1DepositIndex { get; set; }

    [SszField(11)]
    [SszProgressiveList]
    public Validator[]? Validators { get; set; }

    [SszField(12)]
    [SszProgressiveList]
    public ulong[]? Balances { get; set; }

    [SszField(13)]
    [SszVector(65_536)]
    public Hash256[]? RandaoMixes { get; set; }

    [SszField(14)]
    [SszVector(8192)]
    public ulong[]? Slashings { get; set; }

    [SszField(15)]
    [SszProgressiveList]
    public byte[]? PreviousEpochParticipation { get; set; }

    [SszField(16)]
    [SszProgressiveList]
    public byte[]? CurrentEpochParticipation { get; set; }

    [SszField(17)]
    [SszVector(4)]
    public BitArray? JustificationBits { get; set; }

    [SszField(18)]
    public Checkpoint? PreviousJustifiedCheckpoint { get; set; }

    [SszField(19)]
    public Checkpoint? CurrentJustifiedCheckpoint { get; set; }

    [SszField(20)]
    public Checkpoint? FinalizedCheckpoint { get; set; }

    [SszField(21)]
    [SszProgressiveList]
    public ulong[]? InactivityScores { get; set; }

    [SszField(22)]
    public SyncCommittee? CurrentSyncCommittee { get; set; }

    [SszField(23)]
    public SyncCommittee? NextSyncCommittee { get; set; }

    /// <remarks>
    /// [New in Gloas:EIP7732]. Replaces <c>latest_execution_payload_header</c>: under ePBS the
    /// execution payload is no longer embedded in state, only the hash of the last one applied.
    /// </remarks>
    [SszField(24)]
    public Hash256? LatestBlockHash { get; set; }

    [SszField(25)]
    public ulong NextWithdrawalIndex { get; set; }

    [SszField(26)]
    public ulong NextWithdrawalValidatorIndex { get; set; }

    /// <remarks>Unmodified in Gloas: still a plain bounded list, not progressive.</remarks>
    [SszField(27)]
    [SszList(16_777_216)]
    public HistoricalSummary[]? HistoricalSummaries { get; set; }

    [SszField(28)]
    public ulong DepositRequestsStartIndex { get; set; }

    [SszField(29)]
    public ulong DepositBalanceToConsume { get; set; }

    [SszField(30)]
    public ulong ExitBalanceToConsume { get; set; }

    [SszField(31)]
    public ulong EarliestExitEpoch { get; set; }

    [SszField(32)]
    public ulong ConsolidationBalanceToConsume { get; set; }

    [SszField(33)]
    public ulong EarliestConsolidationEpoch { get; set; }

    [SszField(34)]
    [SszProgressiveList]
    public PendingDeposit[]? PendingDeposits { get; set; }

    [SszField(35)]
    [SszProgressiveList]
    public PendingPartialWithdrawal[]? PendingPartialWithdrawals { get; set; }

    [SszField(36)]
    [SszProgressiveList]
    public PendingConsolidation[]? PendingConsolidations { get; set; }

    /// <remarks>Unmodified in Gloas (EIP-7917), unlike its siblings: still a plain fixed vector.</remarks>
    [SszField(37)]
    [SszVector(64)]
    public ulong[]? ProposerLookahead { get; set; }

    /// <remarks>[New in Gloas:EIP7732]. The builder registry.</remarks>
    [SszField(38)]
    [SszProgressiveList]
    public Builder[]? Builders { get; set; }

    /// <remarks>[New in Gloas:EIP7732]. <c>BuilderIndex</c>.</remarks>
    [SszField(39)]
    public ulong NextWithdrawalBuilderIndex { get; set; }

    /// <remarks>
    /// [New in Gloas:EIP7732]. <c>BitVector[SLOTS_PER_HISTORICAL_ROOT]</c>, indexed by slot modulo
    /// <c>SLOTS_PER_HISTORICAL_ROOT</c>; a null value would default to all-zero on encode, which is
    /// wrong for the post-upgrade value (the spec initializes every bit to 1), so callers must set it.
    /// </remarks>
    [SszField(40)]
    [SszVector(8192)]
    public BitArray? ExecutionPayloadAvailability { get; set; }

    /// <remarks>
    /// [New in Gloas:EIP7732]. <c>Vector[BuilderPendingPayment, 2 * SLOTS_PER_EPOCH]</c> — a fixed
    /// vector of composite items, so (unlike a vector of a basic type) a null value does NOT default
    /// to a zero-filled array on encode; callers must supply all 64 entries explicitly.
    /// </remarks>
    [SszField(41)]
    [SszVector(64)]
    public BuilderPendingPayment[]? BuilderPendingPayments { get; set; }

    /// <remarks>[New in Gloas:EIP7732].</remarks>
    [SszField(42)]
    [SszProgressiveList]
    public BuilderPendingWithdrawal[]? BuilderPendingWithdrawals { get; set; }

    /// <remarks>[New in Gloas:EIP7732]. The payload bid the state most recently committed to.</remarks>
    [SszField(43)]
    public ExecutionPayloadBid? LatestExecutionPayloadBid { get; set; }

    /// <remarks>[New in Gloas:EIP7732]. <c>Withdrawals</c>: <c>ProgressiveList[Withdrawal]</c> (EIP-7688).</remarks>
    [SszField(44)]
    [SszProgressiveList]
    public Withdrawal[]? PayloadExpectedWithdrawals { get; set; }

    /// <remarks>
    /// [New in Gloas:EIP7732]. <c>Vector[PayloadTimelinessCommittee, (MIN_SEED_LOOKAHEAD + 2) * SLOTS_PER_EPOCH]</c>
    /// (96 entries on mainnet). A fixed vector of composite items: like <see cref="BuilderPendingPayments"/>,
    /// a null value does not default, so callers must supply all 96 entries.
    /// </remarks>
    [SszField(45)]
    [SszVector(96)]
    public PayloadTimelinessCommittee[]? PtcWindow { get; set; }
}
