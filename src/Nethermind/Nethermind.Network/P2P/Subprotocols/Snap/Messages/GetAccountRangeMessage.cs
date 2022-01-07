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

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetAccountRangeMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.GetAccountRange;

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public Keccak RootHash { get; set; }
        
        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public Keccak StartingHash { get; set; }
        
        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public Keccak LimitHash { get; set; }
        
        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long ResponseBytes { get; set; }
    }
}
