// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Spec;

/// <summary>
/// Preset and configuration constants used by the beacon state transition.
/// </summary>
/// <remarks>
/// Values are sourced from the consensus-specs presets (minimal for the isolated test build)
/// (<c>presets/mainnet/{phase0,altair,bellatrix,capella,deneb,electra,fulu}.yaml</c>) and the parts
/// of <c>configs/mainnet.yaml</c> that affect the state transition. Preset constants that only
/// affect SSZ shapes (list limits, vector lengths) are referenced by the container definitions in
/// <c>Types/</c>, since the SSZ source generator requires compile-time constants there.
/// </remarks>
public static class Presets
{
#if MINIMAL_PRESET
    private const bool Minimal = true;
#else
    private const bool Minimal = false;
#endif
    // Phase0 — misc
    public const int MaxCommitteesPerSlot = Minimal ? 4 : 64;
    public const int TargetCommitteeSize = Minimal ? 4 : 128;
    public const int MaxValidatorsPerCommittee = 2048;
    public const int ShuffleRoundCount = Minimal ? 10 : 90;
    internal const ulong Eth1DataVotesLimit = SlotsPerEpoch * EpochsPerEth1VotingPeriod;
    internal const ulong MaxValidatorsPerSlot = (ulong)MaxValidatorsPerCommittee * MaxCommitteesPerSlot;
    public const ulong HysteresisQuotient = 4;
    public const ulong HysteresisDownwardMultiplier = 1;
    public const ulong HysteresisUpwardMultiplier = 5;

    /// <summary><c>BASIS_POINTS</c> (specs/phase0/fork-choice.md).</summary>
    public const ulong BasisPoints = 10_000;

    /// <summary><c>JUSTIFICATION_BITS_LENGTH</c> (specs/phase0/beacon-chain.md).</summary>
    public const int JustificationBitsLength = 4;

    /// <summary><c>TARGET_AGGREGATORS_PER_COMMITTEE</c> (validator.md is_aggregator).</summary>
    public const ulong TargetAggregatorsPerCommittee = 16;

    // Phase0 — gwei values
    public const ulong MinDepositAmount = 1_000_000_000;
    public const ulong MaxEffectiveBalance = 32_000_000_000;
    public const ulong EffectiveBalanceIncrement = 1_000_000_000;

    // Phase0 — max operations per block
    /// <summary><c>MAX_DEPOSITS</c> (the Eth1 deposits list limit, distinct from EIP-6110 deposit requests).</summary>
    public const ulong MaxDeposits = 16;

    // Phase0 — time parameters
    public const ulong GenesisSlot = 0;
    public const ulong GenesisEpoch = 0;
    public const ulong MinAttestationInclusionDelay = 1;
    public const ulong SlotsPerEpoch = Minimal ? 8 : 32;
    public const ulong MinSeedLookahead = 1;
    public const ulong MaxSeedLookahead = 4;
    public const ulong EpochsPerEth1VotingPeriod = Minimal ? 4 : 64;
    public const ulong SlotsPerHistoricalRoot = Minimal ? 64 : 8192;
    public const ulong MinEpochsToInactivityPenalty = 4;

    // Phase0 — state list lengths
    public const ulong EpochsPerHistoricalVector = Minimal ? 64 : 65_536;
    public const ulong EpochsPerSlashingsVector = Minimal ? 64 : 8192;

    // Phase0 — rewards and penalties
    public const ulong BaseRewardFactor = 64;

    // Altair — participation flag indices and incentivization weights
    public const int TimelySourceFlagIndex = 0;
    public const int TimelyTargetFlagIndex = 1;
    public const int TimelyHeadFlagIndex = 2;
    public const ulong TimelySourceWeight = 14;
    public const ulong TimelyTargetWeight = 26;
    public const ulong TimelyHeadWeight = 14;
    public const ulong SyncRewardWeight = 2;
    public const ulong ProposerWeight = 8;
    public const ulong WeightDenominator = 64;
    public static readonly ulong[] ParticipationFlagWeights = [TimelySourceWeight, TimelyTargetWeight, TimelyHeadWeight];

    // Altair — sync committee
    public const int SyncCommitteeSize = Minimal ? 32 : 512;
    public const ulong EpochsPerSyncCommitteePeriod = Minimal ? 8 : 256;

    // Bellatrix — updated penalty values (still in force for Electra rewards/penalties)
    public const ulong InactivityPenaltyQuotientBellatrix = 16_777_216;
    public const ulong ProportionalSlashingMultiplierBellatrix = 3;

    // Capella — withdrawals
    public const int MaxWithdrawalsPerPayload = Minimal ? 4 : 16;
    public const int MaxValidatorsPerWithdrawalsSweep = Minimal ? 16 : 16_384;

    // Electra — gwei values
    public const ulong MinActivationBalance = 32_000_000_000;
    public const ulong MaxEffectiveBalanceElectra = 2_048_000_000_000;

    // Electra — rewards and penalties
    public const ulong MinSlashingPenaltyQuotientElectra = 4096;
    public const ulong WhistleblowerRewardQuotientElectra = 4096;

    // Electra — state list lengths
    /// <summary>The SSZ list limit of <c>pending_partial_withdrawals</c>.</summary>
    public const int PendingPartialWithdrawalsLimit = Minimal ? 64 : 134_217_728;
    /// <summary>The SSZ list limit of <c>pending_consolidations</c>.</summary>
    public const int PendingConsolidationsLimit = Minimal ? 64 : 262_144;

    // Electra — withdrawals and deposits processing
    public const int MaxPendingPartialsPerWithdrawalsSweep = Minimal ? 2 : 8;
    public const int MaxPendingDepositsPerEpoch = 16;
    public const ulong UnsetDepositRequestsStartIndex = ulong.MaxValue;
    public const ulong FullExitRequestAmount = 0;

    // Fulu — EIP-7917 proposer lookahead
    public const ulong ProposerLookaheadSlots = (MinSeedLookahead + 1) * SlotsPerEpoch;

    // Gloas — misc (specs/gloas/beacon-chain.md "Presets/Misc", fetched from consensus-specs master 2026-09-19)
    public const ulong PtcSize = Minimal ? 16 : 512;
    /// <summary>The <c>BeaconState.builder_pending_payments</c> vector length: <c>2 * SLOTS_PER_EPOCH</c>.</summary>
    public const ulong BuilderPendingPaymentsLength = 2 * SlotsPerEpoch;
    /// <summary>The <c>BeaconState.ptc_window</c> vector length: <c>(MIN_SEED_LOOKAHEAD + 2) * SLOTS_PER_EPOCH</c>.</summary>
    public const ulong PtcWindowLength = (MinSeedLookahead + 2) * SlotsPerEpoch;
    /// <summary><c>BUILDER_PAYMENT_THRESHOLD_NUMERATOR</c> / <c>_DENOMINATOR</c>.</summary>
    public const ulong BuilderPaymentThresholdNumerator = 6;
    public const ulong BuilderPaymentThresholdDenominator = 10;

    // Gloas — withdrawal prefixes and builder versions (specs/gloas/beacon-chain.md "Constants")
    public const byte BuilderWithdrawalPrefix = 0xB0;
    public const byte PayloadBuilderVersion = 0;
    /// <summary><c>BUILDER_INDEX_SELF_BUILD</c>: the sentinel builder index used by self-built (non-ePBS-market) payloads.</summary>
    public const ulong BuilderIndexSelfBuild = ulong.MaxValue;
    /// <summary><c>BUILDER_INDEX_FLAG</c> (<c>Uint64(2**40)</c>): marks a withdrawal's <c>validator_index</c> as a builder index.</summary>
    public const ulong BuilderIndexFlag = 1UL << 40;
    /// <summary><c>MAX_BUILDERS_PER_WITHDRAWALS_SWEEP</c> (<c>2**14</c>).</summary>
    public const int MaxBuildersPerWithdrawalsSweep = Minimal ? 16 : 16_384;

    // Gloas — time parameters
    /// <summary><c>MIN_BUILDER_WITHDRAWABILITY_DELAY</c>, in epochs.</summary>
    public const ulong MinBuilderWithdrawabilityDelay = Minimal ? 2 : 64;

    // Operation list bounds enforced at runtime rather than by the SSZ type (progressive lists,
    // unlike the pre-Gloas bounded lists they replace, carry no compile-time length limit).
    public const int MaxProposerSlashings = 16;
    public const int MaxAttesterSlashingsElectra = 1;
    public const int MaxAttestationsElectra = 8;
    public const int MaxVoluntaryExits = 16;
    public const int MaxBlsToExecutionChanges = 16;
    /// <summary><c>MAX_PAYLOAD_ATTESTATIONS</c> (specs/gloas/beacon-chain.md "Max operations per block").</summary>
    public const int MaxPayloadAttestations = 4;
    public const int MaxDepositRequestsPerPayload = 8_192;
    public const int MaxWithdrawalRequestsPerPayload = 16;
    public const int MaxConsolidationRequestsPerPayload = 2;
    /// <summary><c>MAX_BUILDER_DEPOSIT_REQUESTS_PER_PAYLOAD</c> (specs/gloas/beacon-chain.md "Execution").</summary>
    public const int MaxBuilderDepositRequestsPerPayload = 64;
    /// <summary><c>MAX_BUILDER_EXIT_REQUESTS_PER_PAYLOAD</c> (specs/gloas/beacon-chain.md "Execution").</summary>
    public const int MaxBuilderExitRequestsPerPayload = 16;

    // Config — validator cycle
    public const ulong EjectionBalance = 16_000_000_000;
    public const ulong MinPerEpochChurnLimit = Minimal ? 2 : 4;
    public const ulong MaxPerEpochActivationChurnLimit = Minimal ? 4 : 8;
    public const ulong ChurnLimitQuotient = Minimal ? 32 : 65_536;
    public const ulong MinPerEpochChurnLimitElectra = Minimal ? 64_000_000_000 : 128_000_000_000;
    public const ulong MaxPerEpochActivationExitChurnLimit = Minimal ? 128_000_000_000 : 256_000_000_000;

    // Gloas - EIP-8061 validator cycle (specs/gloas/beacon-chain.md "Configuration/Validator cycle")
    public const ulong ChurnLimitQuotientGloas = Minimal ? 16UL : 1UL << 15;
    public const ulong ConsolidationChurnLimitQuotient = Minimal ? 32UL : 1UL << 16;
    public const ulong MaxPerEpochActivationChurnLimitGloas = Minimal ? 128_000_000_000 : 256_000_000_000;

    // Config — time parameters
    public const ulong SecondsPerSlot = Minimal ? 6 : 12;
    /// <summary>Phase0 fork-choice <c>INTERVALS_PER_SLOT</c>: a block is timely (proposer boost) when it arrives in the first interval of its slot.</summary>
    public const ulong IntervalsPerSlot = 3;
    public const ulong MinValidatorWithdrawabilityDelay = 256;
    public const ulong ShardCommitteePeriod = Minimal ? 64 : 256;

    // Config — inactivity scores
    public const ulong InactivityScoreBias = 4;
    public const ulong InactivityScoreRecoveryRate = 16;

    // Constants
    public const ulong FarFutureEpoch = ulong.MaxValue;
    public const int DepositContractTreeDepth = 32;
    public const byte BlsWithdrawalPrefix = 0x00;
    public const byte EthWithdrawalPrefix = 0x01;
    public const byte CompoundingWithdrawalPrefix = 0x02;
}
