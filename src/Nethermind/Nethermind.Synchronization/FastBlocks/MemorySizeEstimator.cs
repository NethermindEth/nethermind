//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class MemorySizeEstimator
    {
        public long EstimateSize(BlockBody blockBody)
        {
            long estimate = 80L;
            estimate += (blockBody.Transactions?.Length ?? 0) * 8L;
            estimate += (blockBody.Ommers?.Length ?? 0) * 8L;
            
            foreach (Transaction transaction in blockBody?.Transactions ?? Array.Empty<Transaction>())
            {
                estimate += EstimateSize(transaction);
            }
            
            foreach (BlockHeader header in blockBody?.Ommers ?? Array.Empty<BlockHeader>())
            {
                estimate += EstimateSize(header);
            }

            return estimate;
        }
        
        /// <summary>
        /// Rough header memory size estimator
        /// </summary>
        /// <param name="header"></param>
        /// <returns></returns>
        public long EstimateSize(BlockHeader header)
        {
            if (header == null)
            {
                return 8;
            }

            return 1212 + (header.ExtraData?.Length ?? 0);
        }
        
        public long EstimateSize(Transaction transaction)
        {
            if (transaction == null)
            {
                return 8;
            }

            return 408 + (transaction.Data?.Length ?? 0) + (transaction.Init?.Length ?? 0);
        }
    }
}