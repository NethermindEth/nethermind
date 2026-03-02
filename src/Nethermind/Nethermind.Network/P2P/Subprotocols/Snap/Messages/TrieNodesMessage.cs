// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessage(IByteArrayList? data) : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.TrieNodes;

        public IByteArrayList Nodes { get; } = data ?? EmptyByteArrayList.Instance;

        public override void Dispose()
        {
            base.Dispose();
            Nodes.Dispose();
        }
    }
}
