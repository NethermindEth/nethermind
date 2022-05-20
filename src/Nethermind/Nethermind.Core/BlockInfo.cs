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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [Flags]
    public enum BlockMetadata
    {
        None = 0x0,
        Finalized = 1,
        Invalid = 2,
        BeaconHeader = 4,
        BeaconBody = 8,
        BeaconMainChain = 16
    }
    
    public class BlockInfo
    {
        public BlockInfo(Keccak blockHash, in UInt256 totalDifficulty, BlockMetadata metadata = BlockMetadata.None)
        {
            BlockHash = blockHash;
            TotalDifficulty = totalDifficulty;
            Metadata = metadata;
        }
        
        public UInt256 TotalDifficulty { get; set; }
        
        public bool WasProcessed { get; set; }

        public Keccak BlockHash { get; }

        public bool IsFinalized
        {
            get => (Metadata & BlockMetadata.Finalized) == BlockMetadata.Finalized;
            set
            {
                if (value)
                {
                    Metadata |= BlockMetadata.Finalized;
                }
                else
                {
                    Metadata &= ~BlockMetadata.Finalized;
                }
            }
        }

        public BlockMetadata Metadata { get; set; }

        /// <summary>
        /// This property is not serialized
        /// </summary>
        public long BlockNumber { get; set; }
    }
}
