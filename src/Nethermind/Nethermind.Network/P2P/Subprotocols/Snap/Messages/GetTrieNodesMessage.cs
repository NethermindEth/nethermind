// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
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
        public ValueHash256 RootHash { get; set; }

        /// <summary>
        /// Trie paths to retrieve the nodes for, grouped by account
        /// </summary>
        public IOwnedReadOnlyList<PathGroup> Paths { get; set; }

        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long Bytes { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            Paths?.Dispose();
        }
    }
}
