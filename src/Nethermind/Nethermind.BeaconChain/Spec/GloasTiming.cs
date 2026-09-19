// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Spec;

/// <summary>
/// Millisecond, basis-point slot timing (<c>configs/mainnet.yaml</c> on ethereum/consensus-specs
/// `master`, fetched 2026-09-19). Gloas expresses slot deadlines directly in milliseconds via a
/// basis-points-of-slot fraction instead of the pre-Gloas <c>SECONDS_PER_SLOT</c> thirds/quarters
/// split, and moves the existing attestation-related deadlines earlier in the slot to make room for
/// the new payload phase (bid at slot start, envelope by <see cref="PayloadDueBps"/>, PTC vote by
/// <see cref="PayloadAttestationDueBps"/>).
/// </summary>
/// <remarks>
/// This is an additive capability, not a replacement for the existing second-granularity slot clock
/// (<see cref="BeaconChainSpec.SecondsPerSlot"/> / <see cref="BeaconChainSpec.GetSlotAtTime"/>): nothing
/// here changes what those consume. A caller that needs a Gloas-era deadline computes it with
/// <see cref="DeadlineMs(ulong)"/> or one of the named helpers instead.
/// </remarks>
public static class GloasTiming
{
    /// <summary>Slot length in milliseconds, replacing <c>SECONDS_PER_SLOT * 1000</c> as the unit Gloas deadlines are expressed in.</summary>
    public const ulong SlotDurationMs = 12_000;

    // Pre-Gloas deadlines, unchanged, expressed in basis points of SLOT_DURATION_MS for comparison.
    public const ulong ProposerReorgCutoffBps = 1_667;
    public const ulong AttestationDueBps = 3_333;
    public const ulong AggregateDueBps = 6_667;
    public const ulong SyncMessageDueBps = 3_333;
    public const ulong ContributionDueBps = 6_667;

    // Gloas: attestation-related deadlines move earlier in the slot.
    public const ulong AttestationDueBpsGloas = 2_500;
    public const ulong AggregateDueBpsGloas = 5_000;
    public const ulong SyncMessageDueBpsGloas = 2_500;
    public const ulong ContributionDueBpsGloas = 5_000;

    // Gloas: new payload-phase deadlines (EIP-7732).
    /// <summary>Deadline for the execution payload envelope to arrive: 50% of the slot, 6000ms into a 12s slot.</summary>
    public const ulong PayloadDueBps = 5_000;
    /// <summary>Deadline for PTC payload-timeliness attestations: 75% of the slot, 9000ms into a 12s slot.</summary>
    public const ulong PayloadAttestationDueBps = 7_500;
    /// <summary>Inclusion-list deadline, unchanged basis-point value from pre-Gloas but listed here for completeness.</summary>
    public const ulong InclusionListDueBps = 6_667;

    /// <summary>
    /// The number of milliseconds into a <see cref="SlotDurationMs"/>-long slot that
    /// <paramref name="basisPoints"/> (out of 10,000) falls at, e.g.
    /// <c>DeadlineMs(PayloadDueBps)</c> is 6000.
    /// </summary>
    public static ulong DeadlineMs(ulong basisPoints) => SlotDurationMs * basisPoints / 10_000;
}
