// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class StorageRangeMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.StorageRanges;

        /// <summary>
        /// List of list of consecutive slots from the trie (one list per account)
        /// </summary>
        public IOwnedReadOnlyList<PathWithStorageSlot[]> Slots { get; set; }

        /// <summary>
        /// List of trie nodes proving the slot range
        /// </summary>
        public IOwnedReadOnlyList<byte[]> Proofs { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            Slots?.Dispose();
            Proofs?.Dispose();
        }
    }
}
