// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Electra <c>SingleAttestation</c>: one validator's unaggregated vote, as published on <c>beacon_attestation_{subnet_id}</c>.</summary>
[SszContainer]
public partial class SingleAttestation
{
    public ulong CommitteeIndex { get; set; }
    public ulong AttesterIndex { get; set; }
    public AttestationData? Data { get; set; }
    public BlsSignature Signature { get; set; }
}

/// <summary>Altair <c>SyncCommitteeMessage</c>.</summary>
[SszContainer]
public partial class SyncCommitteeMessage
{
    public ulong Slot { get; set; }
    public Hash256? BeaconBlockRoot { get; set; }
    public ulong ValidatorIndex { get; set; }
    public BlsSignature Signature { get; set; }
}

/// <summary>Altair <c>SyncCommitteeContribution</c>.</summary>
[SszContainer]
public partial class SyncCommitteeContribution
{
    /// <summary>Altair <c>SYNC_COMMITTEE_SUBNET_COUNT</c>.</summary>
    public const int SubnetCount = 4;

    /// <summary>Members of one subcommittee: <c>SYNC_COMMITTEE_SIZE // SYNC_COMMITTEE_SUBNET_COUNT</c>.</summary>
    public const int SubcommitteeSize = Presets.SyncCommitteeSize / SubnetCount;

    public ulong Slot { get; set; }
    public Hash256? BeaconBlockRoot { get; set; }
    public ulong SubcommitteeIndex { get; set; }
    [SszVector(SubcommitteeSize)]
    public BitArray? AggregationBits { get; set; }
    public BlsSignature Signature { get; set; }
}

/// <summary>Altair <c>ContributionAndProof</c>.</summary>
[SszContainer]
public partial class ContributionAndProof
{
    public ulong AggregatorIndex { get; set; }
    public SyncCommitteeContribution? Contribution { get; set; }
    public BlsSignature SelectionProof { get; set; }
}

/// <summary>Altair <c>SignedContributionAndProof</c>.</summary>
[SszContainer]
public partial class SignedContributionAndProof
{
    public ContributionAndProof? Message { get; set; }
    public BlsSignature Signature { get; set; }
}
