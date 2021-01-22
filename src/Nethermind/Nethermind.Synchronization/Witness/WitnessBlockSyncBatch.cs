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

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.Witness
{
    public class WitnessBlockSyncBatch
    {
        public WitnessBlockSyncBatch(Keccak blockHash, long blockNumber, DateTime timestamp)
        {
            BlockHash = blockHash;
            BlockNumber = blockNumber;
            Timestamp = timestamp;
        }
        
        public Keccak BlockHash { get; }
        public long BlockNumber { get; }
        
        public DateTime Timestamp { get; set; }

        public int Retry { get; set; }
        
        public Memory<Keccak>? Response { get; set; }
        
        public override string ToString()
        {
            return $"block {BlockHash} witnesses requests with {Response?.Length ?? 0} node response";
        }
    }
}
