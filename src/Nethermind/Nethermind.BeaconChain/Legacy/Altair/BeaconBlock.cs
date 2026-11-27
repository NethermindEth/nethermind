// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Altair;

/// <summary>Altair <c>BeaconBlockBody</c>.</summary>
[SszContainer]
public partial class BeaconBlockBody : Nethermind.BeaconChain.Legacy.Bellatrix.BeaconBlockBody
{
    [SszIgnore]
    public new Bellatrix.ExecutionPayload? ExecutionPayload { get => base.ExecutionPayload; set => base.ExecutionPayload = value; }
}

/// <summary>Altair <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock : Nethermind.BeaconChain.Legacy.Bellatrix.BeaconBlock
{
    public new BeaconBlockBody? Body
    {
        get => (BeaconBlockBody?)base.Body;
        set => base.Body = value;
    }
}

/// <summary>Altair <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock : Nethermind.BeaconChain.Legacy.Bellatrix.SignedBeaconBlock
{
    public new BeaconBlock? Message
    {
        get => (BeaconBlock?)base.Message;
        set => base.Message = value;
    }
}
