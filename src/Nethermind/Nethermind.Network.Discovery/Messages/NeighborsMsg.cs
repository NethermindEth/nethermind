// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Messages;

public class NeighborsMsg : DiscoveryMsg
{
    public Node[] Nodes { get; init; }

    public NeighborsMsg(IPEndPoint farAddress, long expirationTime, Node[] nodes) : base(farAddress, expirationTime)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }

    public NeighborsMsg(PublicKey farPublicKey, long expirationTime, Node[] nodes) : base(farPublicKey, expirationTime)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }

    public override string ToString()
    {
        return base.ToString() + $", Nodes: {(Nodes.Any() ? string.Join(",", Nodes.Select(x => x.ToString())) : "empty")}";
    }

    public override MsgType MsgType => MsgType.Neighbors;
}
