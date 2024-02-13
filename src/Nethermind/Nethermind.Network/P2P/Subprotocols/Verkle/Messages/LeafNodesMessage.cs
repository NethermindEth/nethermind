// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class LeafNodesMessage : VerkleMessageBase
{
    public LeafNodesMessage() { }
    public LeafNodesMessage(byte[][]? data)
    {
        Nodes = data ?? Array.Empty<byte[]>();
    }

    public override int PacketType => VerkleMessageCode.LeafNodes;

    public byte[][] Nodes { get; set; }
}
