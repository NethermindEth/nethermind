// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Altair <c>SyncCommittee</c>.</summary>
[SszContainer]
public partial class SyncCommittee
{
    [SszVector(512)]
    public BlsPublicKey[]? Pubkeys { get; set; }

    public BlsPublicKey AggregatePubkey { get; set; }
}

/// <summary>Altair <c>SyncAggregate</c>.</summary>
[SszContainer]
public partial class SyncAggregate
{
    [SszVector(512)]
    public BitArray? SyncCommitteeBits { get; set; }

    public BlsSignature SyncCommitteeSignature { get; set; }
}
