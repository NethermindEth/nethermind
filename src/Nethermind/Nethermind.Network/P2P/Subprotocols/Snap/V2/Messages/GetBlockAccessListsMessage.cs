// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class GetBlockAccessListsMessage : SnapMessageBase
    {
        public override int PacketType => Snap2MessageCode.GetBlockAccessLists;

        /// <summary>
        /// Block hashes to retrieve the block access lists for
        /// </summary>
        public IOwnedReadOnlyList<ValueHash256> BlockHashes { get; set; }

        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long Bytes { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            BlockHashes?.Dispose();
        }
    }
}
