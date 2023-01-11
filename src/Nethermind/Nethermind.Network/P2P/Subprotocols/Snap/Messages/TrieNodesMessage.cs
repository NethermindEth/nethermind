// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessage : SnapMessageBase
    {
        public TrieNodesMessage(byte[][]? data)
        {
            Nodes = data ?? Array.Empty<byte[]>();
        }

        public override int PacketType => SnapMessageCode.TrieNodes;

        public byte[][] Nodes { get; set; }
    }
}
