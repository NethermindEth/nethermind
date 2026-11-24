// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Bellatrix;

/// <summary>Bellatrix <c>BeaconBlockBody</c>.</summary>
[SszContainer]
public partial class BeaconBlockBody : Nethermind.BeaconChain.Legacy.Capella.BeaconBlockBody
{
    public new ExecutionPayload? ExecutionPayload
    {
        get => (ExecutionPayload?)base.ExecutionPayload;
        set => base.ExecutionPayload = value;
    }
    [SszIgnore]
    public new SignedBlsToExecutionChange[]? BlsToExecutionChanges { get => base.BlsToExecutionChanges; set => base.BlsToExecutionChanges = value; }
}

/// <summary>Bellatrix <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock : Nethermind.BeaconChain.Legacy.Capella.BeaconBlock
{
    public new BeaconBlockBody? Body
    {
        get => (BeaconBlockBody?)base.Body;
        set => base.Body = value;
    }
}

/// <summary>Bellatrix <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock : Nethermind.BeaconChain.Legacy.Capella.SignedBeaconBlock
{
    public new BeaconBlock? Message
    {
        get => (BeaconBlock?)base.Message;
        set => base.Message = value;
    }
}
