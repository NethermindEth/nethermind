//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
