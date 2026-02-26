// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessage(RlpByteArrayList? data) : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.TrieNodes;

        public RlpByteArrayList? Nodes { get; } = data;

        public override void Dispose()
        {
            base.Dispose();
            Nodes?.Dispose();
        }
    }
}
