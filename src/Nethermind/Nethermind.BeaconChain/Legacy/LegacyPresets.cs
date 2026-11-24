// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Legacy;

/// <summary>Preset constants that only forks before Electra use.</summary>
internal static class LegacyPresets
{
#if MINIMAL_PRESET
    private const bool Minimal = true;
#else
    private const bool Minimal = false;
#endif

    // Phase0
    public const ulong WhistleblowerRewardQuotient = 512;
    public const ulong ProposerRewardQuotient = 8;
    public const ulong BaseRewardsPerEpoch = 4;
    public const ulong InactivityPenaltyQuotient = Minimal ? 33_554_432 : 67_108_864;
    public const ulong MinSlashingPenaltyQuotient = Minimal ? 64 : 128;
    public const ulong ProportionalSlashingMultiplier = Minimal ? 2 : 1;
    public const ulong MaxRandomByte = byte.MaxValue;
    public const int MaxAttesterSlashings = 2;
    public const int MaxAttestations = 128;
    public const ulong MaxPendingAttestations = MaxAttestations * Presets.DefaultSlotsPerEpoch;

    // Altair
    public const ulong InactivityPenaltyQuotientAltair = 50_331_648;
    public const ulong MinSlashingPenaltyQuotientAltair = 64;
    public const ulong ProportionalSlashingMultiplierAltair = 2;

    // Bellatrix
    public const ulong MinSlashingPenaltyQuotientBellatrix = 32;

    // Deneb
    public const ulong MaxBlobsPerBlockDeneb = 6;
}
