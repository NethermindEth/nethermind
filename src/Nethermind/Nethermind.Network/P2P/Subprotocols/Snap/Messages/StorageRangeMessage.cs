// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class StorageRangeMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.StorageRanges;

        /// <summary>
        /// List of list of consecutive slots from the trie (one list per account)
        /// </summary>
        public PathWithStorageSlot[][] Slots { get; set; }

        /// <summary>
        /// List of trie nodes proving the slot range
        /// </summary>
        public byte[][] Proofs { get; set; }
    }
}
