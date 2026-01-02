// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Messages;

public class NeighborsMsg : DiscoveryMsg
{
    public ArraySegment<Node> Nodes { get; init; }

    public NeighborsMsg(IPEndPoint farAddress, long expirationTime, ArraySegment<Node> nodes) : base(farAddress, expirationTime)
    {
        Nodes = nodes;
    }

    public NeighborsMsg(PublicKey farPublicKey, long expirationTime, ArraySegment<Node> nodes) : base(farPublicKey, expirationTime)
    {
        Nodes = nodes;
    }

    public override string ToString()
    {
        return base.ToString() + $", Nodes: {(Nodes.Count != 0 ? string.Join(",", Nodes.Select(static x => x.ToString())) : "empty")}";
    }

    public override MsgType MsgType => MsgType.Neighbors;
}
