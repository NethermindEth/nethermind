// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Phase0 <c>AttestationData</c>.</summary>
[SszContainer]
public partial class AttestationData
{
    public ulong Slot { get; set; }

    public ulong Index { get; set; }

    public Hash256? BeaconBlockRoot { get; set; }

    public Checkpoint? Source { get; set; }

    public Checkpoint? Target { get; set; }
}

/// <summary>Electra <c>IndexedAttestation</c>.</summary>
[SszContainer]
public partial class IndexedAttestation
{
    /// <remarks>Limit is <c>MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT</c> (EIP-7549).</remarks>
    [SszList(131_072)]
    public ulong[]? AttestingIndices { get; set; }

    public AttestationData? Data { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Electra <c>Attestation</c> (EIP-7549 on-chain aggregate).</summary>
[SszContainer]
public partial class Attestation
{
    [SszList(131_072)]
    public BitArray? AggregationBits { get; set; }

    public AttestationData? Data { get; set; }

    public BlsSignature Signature { get; set; }

    [SszVector(64)]
    public BitArray? CommitteeBits { get; set; }
}

/// <summary>Electra <c>AttesterSlashing</c>.</summary>
[SszContainer]
public partial class AttesterSlashing
{
    public IndexedAttestation? Attestation1 { get; set; }

    public IndexedAttestation? Attestation2 { get; set; }
}

/// <summary>Phase0 <c>ProposerSlashing</c>.</summary>
[SszContainer]
public partial class ProposerSlashing
{
    public SignedBeaconBlockHeader? SignedHeader1 { get; set; }

    public SignedBeaconBlockHeader? SignedHeader2 { get; set; }
}

/// <summary>Electra <c>AggregateAndProof</c>.</summary>
[SszContainer]
public partial class AggregateAndProof
{
    public ulong AggregatorIndex { get; set; }

    public Attestation? Aggregate { get; set; }

    public BlsSignature SelectionProof { get; set; }
}

/// <summary>Electra <c>SignedAggregateAndProof</c>.</summary>
[SszContainer]
public partial class SignedAggregateAndProof
{
    public AggregateAndProof? Message { get; set; }

    public BlsSignature Signature { get; set; }
}
