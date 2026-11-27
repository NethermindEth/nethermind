// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy;

/// <summary>Phase0 to Deneb <c>Attestation</c>, one committee each.</summary>
[SszContainer]
public partial class Attestation
{
    [SszList(Presets.MaxValidatorsPerCommittee)]
    public BitArray? AggregationBits { get; set; }
    public AttestationData? Data { get; set; }
    public BlsSignature Signature { get; set; }
}

/// <summary>Phase0 to Deneb <c>IndexedAttestation</c>.</summary>
[SszContainer]
public partial class IndexedAttestation
{
    [SszList(Presets.MaxValidatorsPerCommittee)]
    public ulong[]? AttestingIndices { get; set; }
    public AttestationData? Data { get; set; }
    public BlsSignature Signature { get; set; }
}

/// <summary>Phase0 to Deneb <c>AttesterSlashing</c>.</summary>
[SszContainer]
public partial class AttesterSlashing
{
    public IndexedAttestation? Attestation1 { get; set; }
    public IndexedAttestation? Attestation2 { get; set; }
}
