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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data
{
    public class PreparePayloadRequest
    {
        public PreparePayloadRequest(Keccak parentHash, UInt256 timestamp, Keccak random, Address feeRecipient)
        {
            ParentHash = parentHash;
            Timestamp = timestamp;
            Random = random;
            FeeRecipient = feeRecipient;
        }

        public Keccak ParentHash { get; set; }
        public UInt256 Timestamp { get; set; }
        public Keccak Random { get; set; }
        public Address FeeRecipient { get; set; }
    }
}
