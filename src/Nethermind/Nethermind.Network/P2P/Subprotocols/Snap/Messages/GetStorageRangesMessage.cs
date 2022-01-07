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

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetStorageRangesMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.GetStorageRanges;
        
        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public Keccak RootHash { get; set; }

        /// <summary>
        /// Account hashes of the storage tries to serve
        /// </summary>
        public Keccak[] AccountHashes { get; set; }
        
        /// <summary>
        /// Storage slot hash of the first to retrieve
        /// </summary>
        public Keccak StartingHash { get; set; }
        
        /// <summary>
        /// Storage slot hash after which to stop serving
        /// </summary>
        public Keccak LimitHash { get; set; }
        
        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long ResponseBytes { get; set; }
    }
}
