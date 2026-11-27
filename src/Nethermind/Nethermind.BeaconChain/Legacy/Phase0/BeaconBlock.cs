// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Phase0;

/// <summary>Phase0 <c>BeaconBlockBody</c>.</summary>
[SszContainer]
public partial class BeaconBlockBody : Nethermind.BeaconChain.Legacy.Altair.BeaconBlockBody
{
    [SszIgnore]
    public new SyncAggregate? SyncAggregate { get => base.SyncAggregate; set => base.SyncAggregate = value; }
}

/// <summary>Phase0 <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock : Nethermind.BeaconChain.Legacy.Altair.BeaconBlock
{
    public new BeaconBlockBody? Body
    {
        get => (BeaconBlockBody?)base.Body;
        set => base.Body = value;
    }
}

/// <summary>Phase0 <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock : Nethermind.BeaconChain.Legacy.Altair.SignedBeaconBlock
{
    public new BeaconBlock? Message
    {
        get => (BeaconBlock?)base.Message;
        set => base.Message = value;
    }
}
