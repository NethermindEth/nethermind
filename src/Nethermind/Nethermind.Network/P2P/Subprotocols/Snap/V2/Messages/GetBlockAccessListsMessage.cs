// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    /// <summary>
    /// snap/2 <c>GetBlockAccessLists (0x08)</c> request (EIP-8189). Requests block access lists for the given block
    /// hashes so a syncing node can apply state diffs for blocks that advanced during bulk state download.
    /// </summary>
    public class GetBlockAccessListsMessage : SnapMessageBase
    {
        public override int PacketType => Snap2MessageCode.GetBlockAccessLists;

        /// <summary>
        /// Block hashes to retrieve the block access lists for. Requests are keyed by block hash so both canonical
        /// and orphaned blocks can be served.
        /// </summary>
        public IOwnedReadOnlyList<ValueHash256> BlockHashes { get; set; }

        /// <summary>
        /// Soft limit, in bytes, on the cumulative size of the response. Recommended default 2 MiB.
        /// </summary>
        public long Bytes { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            BlockHashes?.Dispose();
        }
    }
}
