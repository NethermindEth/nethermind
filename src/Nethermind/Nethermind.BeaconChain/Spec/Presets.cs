// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Spec;

/// <summary>
/// Mainnet preset and configuration constants used by the Electra/Fulu state transition.
/// </summary>
/// <remarks>
/// Values are sourced from the consensus-specs mainnet presets
/// (<c>presets/mainnet/{phase0,altair,bellatrix,capella,deneb,electra,fulu}.yaml</c>) and the parts
/// of <c>configs/mainnet.yaml</c> that affect the state transition. Preset constants that only
/// affect SSZ shapes (list limits, vector lengths) live in the container definitions in
/// <c>Types/</c>, since the SSZ source generator requires compile-time constants there.
/// </remarks>
public static class Presets
{
    // Phase0 — misc
    public const int MaxCommitteesPerSlot = 64;
    public const int TargetCommitteeSize = 128;
    public const int MaxValidatorsPerCommittee = 2048;
    public const int ShuffleRoundCount = 90;
    public const ulong HysteresisQuotient = 4;
    public const ulong HysteresisDownwardMultiplier = 1;
    public const ulong HysteresisUpwardMultiplier = 5;

    // Phase0 — gwei values
    public const ulong MinDepositAmount = 1_000_000_000;
    public const ulong MaxEffectiveBalance = 32_000_000_000;
    public const ulong EffectiveBalanceIncrement = 1_000_000_000;

    // Phase0 — time parameters
    public const ulong GenesisSlot = 0;
    public const ulong GenesisEpoch = 0;
    public const ulong MinAttestationInclusionDelay = 1;
    public const ulong SlotsPerEpoch = 32;
    public const ulong MinSeedLookahead = 1;
    public const ulong MaxSeedLookahead = 4;
    public const ulong EpochsPerEth1VotingPeriod = 64;
    public const ulong SlotsPerHistoricalRoot = 8192;
    public const ulong MinEpochsToInactivityPenalty = 4;

    // Phase0 — state list lengths
    public const ulong EpochsPerHistoricalVector = 65_536;
    public const ulong EpochsPerSlashingsVector = 8192;

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
    public const int SyncCommitteeSize = 512;
    public const ulong EpochsPerSyncCommitteePeriod = 256;

    // Bellatrix — updated penalty values (still in force for Electra rewards/penalties)
    public const ulong InactivityPenaltyQuotientBellatrix = 16_777_216;
    public const ulong ProportionalSlashingMultiplierBellatrix = 3;

    // Capella — withdrawals
    public const int MaxWithdrawalsPerPayload = 16;
    public const int MaxValidatorsPerWithdrawalsSweep = 16_384;

    // Electra — gwei values
    public const ulong MinActivationBalance = 32_000_000_000;
    public const ulong MaxEffectiveBalanceElectra = 2_048_000_000_000;

    // Electra — rewards and penalties
    public const ulong MinSlashingPenaltyQuotientElectra = 4096;
    public const ulong WhistleblowerRewardQuotientElectra = 4096;

    // Electra — withdrawals and deposits processing
    public const int MaxPendingPartialsPerWithdrawalsSweep = 8;
    public const int MaxPendingDepositsPerEpoch = 16;
    public const ulong UnsetDepositRequestsStartIndex = ulong.MaxValue;
    public const ulong FullExitRequestAmount = 0;

    // Fulu — EIP-7917 proposer lookahead
    public const ulong ProposerLookaheadSlots = (MinSeedLookahead + 1) * SlotsPerEpoch;

    // Config — validator cycle
    public const ulong EjectionBalance = 16_000_000_000;
    public const ulong MinPerEpochChurnLimit = 4;
    public const ulong MaxPerEpochActivationChurnLimit = 8;
    public const ulong ChurnLimitQuotient = 65_536;
    public const ulong MinPerEpochChurnLimitElectra = 128_000_000_000;
    public const ulong MaxPerEpochActivationExitChurnLimit = 256_000_000_000;

    // Config — time parameters
    public const ulong MinValidatorWithdrawabilityDelay = 256;
    public const ulong ShardCommitteePeriod = 256;

    // Config — inactivity scores
    public const ulong InactivityScoreBias = 4;
    public const ulong InactivityScoreRecoveryRate = 16;

    // Constants
    public const ulong FarFutureEpoch = ulong.MaxValue;
    public const int DepositContractTreeDepth = 32;
    public const byte BlsWithdrawalPrefix = 0x00;
    public const byte EthWithdrawalPrefix = 0x01;
    public const byte CompoundingWithdrawalPrefix = 0x02;
    public static readonly byte[] GenesisForkVersion = [0x00, 0x00, 0x00, 0x00];
}
