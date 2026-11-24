// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Capella;

/// <summary>Capella <c>BeaconBlockBody</c>.</summary>
[SszContainer]
public partial class BeaconBlockBody : Nethermind.BeaconChain.Legacy.Deneb.BeaconBlockBody
{
    public new ExecutionPayload? ExecutionPayload
    {
        get => (ExecutionPayload?)base.ExecutionPayload;
        set => base.ExecutionPayload = value;
    }
    [SszIgnore]
    public new SszKzgCommitment[]? BlobKzgCommitments { get => base.BlobKzgCommitments; set => base.BlobKzgCommitments = value; }
}

/// <summary>Capella <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock : Nethermind.BeaconChain.Legacy.Deneb.BeaconBlock
{
    public new BeaconBlockBody? Body
    {
        get => (BeaconBlockBody?)base.Body;
        set => base.Body = value;
    }
}

/// <summary>Capella <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock : Nethermind.BeaconChain.Legacy.Deneb.SignedBeaconBlock
{
    public new BeaconBlock? Message
    {
        get => (BeaconBlock?)base.Message;
        set => base.Message = value;
    }
}
