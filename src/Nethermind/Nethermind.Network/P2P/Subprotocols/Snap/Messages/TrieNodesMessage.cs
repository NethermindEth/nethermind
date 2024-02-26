// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessage : SnapMessageBase
    {
        public TrieNodesMessage(IDisposableReadOnlyList<byte[]>? data)
        {
            Nodes = data ?? ArrayPoolList<byte[]>.Empty();
        }

        public override int PacketType => SnapMessageCode.TrieNodes;

        public IDisposableReadOnlyList<byte[]> Nodes { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            Nodes.Dispose();
        }
    }
}
