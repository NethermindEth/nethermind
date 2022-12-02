// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Messages;

public class FindNodeMsg : DiscoveryMsg
{
    public byte[] SearchedNodeId { get; set; }

    public override string ToString()
    {
        return base.ToString() + $", SearchedNodeId: {SearchedNodeId.ToHexString()}";
    }

    public override MsgType MsgType => MsgType.FindNode;

    public FindNodeMsg(IPEndPoint farAddress, long expirationDate, byte[] searchedNodeId)
        : base(farAddress, expirationDate)
    {
        SearchedNodeId = searchedNodeId;
    }

    public FindNodeMsg(PublicKey farPublicKey, long expirationDate, byte[] searchedNodeId)
        : base(farPublicKey, expirationDate)
    {
        SearchedNodeId = searchedNodeId;
    }
}
