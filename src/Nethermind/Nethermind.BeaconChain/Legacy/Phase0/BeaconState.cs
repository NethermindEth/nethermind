// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Phase0;

/// <summary>Phase0 <c>PendingAttestation</c>.</summary>
[SszContainer]
public partial class PendingAttestation
{
    [SszList(Presets.MaxValidatorsPerCommittee)]
    public BitArray? AggregationBits { get; set; }
    public AttestationData? Data { get; set; }
    public ulong InclusionDelay { get; set; }
    public ulong ProposerIndex { get; set; }
}

/// <summary>Phase0 <c>BeaconState</c>.</summary>
/// <remarks>
/// The pending attestation lists hide the participation lists Altair replaced them with, which keeps their field positions.
/// </remarks>
[SszContainer]
public partial class BeaconState : Nethermind.BeaconChain.Legacy.Altair.BeaconState
{
    /// <summary><c>previous_epoch_attestations</c>.</summary>
    [SszList(LegacyPresets.MaxPendingAttestations)]
    public new PendingAttestation[]? PreviousEpochParticipation { get; set; }
    /// <summary><c>current_epoch_attestations</c>.</summary>
    [SszList(LegacyPresets.MaxPendingAttestations)]
    public new PendingAttestation[]? CurrentEpochParticipation { get; set; }
    [SszIgnore]
    public new ulong[]? InactivityScores { get => base.InactivityScores; set => base.InactivityScores = value; }
    [SszIgnore]
    public new SyncCommittee? CurrentSyncCommittee { get => base.CurrentSyncCommittee; set => base.CurrentSyncCommittee = value; }
    [SszIgnore]
    public new SyncCommittee? NextSyncCommittee { get => base.NextSyncCommittee; set => base.NextSyncCommittee = value; }
}
