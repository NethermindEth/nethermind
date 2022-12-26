// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessage : SnapMessageBase
    {
        public TrieNodesMessage(byte[][]? data)
        {
            Nodes = data ?? Array.Empty<byte[]>();
        }

        public TrieNodesMessage(List<byte[]>? data)
        {
            Nodes = data ?? new List<byte[]>();
        }

        public override int PacketType => SnapMessageCode.TrieNodes;

        public IReadOnlyList<byte[]> Nodes { get; set; }
    }
}
