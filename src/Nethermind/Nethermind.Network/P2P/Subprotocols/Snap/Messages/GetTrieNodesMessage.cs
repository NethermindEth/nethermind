// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.GetTrieNodes;

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public ValueKeccak RootHash { get; set; }

        /// <summary>
        /// Trie paths to retrieve the nodes for, grouped by account
        /// </summary>
        public PathGroup[] Paths { get; set; }

        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long Bytes { get; set; }
    }
}
