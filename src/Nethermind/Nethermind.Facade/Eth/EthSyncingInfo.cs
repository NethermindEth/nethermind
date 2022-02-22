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

using Nethermind.Blockchain.Find;

namespace Nethermind.Facade.Eth
{
    public class EthSyncingInfo : IEthSyncingInfo
    {
        private readonly IBlockFinder _blockFinder;

        public EthSyncingInfo(IBlockFinder blockFinder)
        {
            _blockFinder = blockFinder;
        }
        
        public SyncingResult GetFullInfo()
        {
            SyncingResult result;
            long bestSuggestedNumber = _blockFinder.FindBestSuggestedHeader().Number;

            long headNumberOrZero = _blockFinder.Head?.Number ?? 0;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;

            if (isSyncing)
            {
                result = new SyncingResult
                {
                    CurrentBlock = headNumberOrZero,
                    HighestBlock = bestSuggestedNumber,
                    StartingBlock = 0L,
                    IsSyncing = true
                };
            }
            else
            {
                result = SyncingResult.NotSyncing;
            }

            return result;
        }
        
        public bool IsSyncing()
        {
            long bestSuggestedNumber = _blockFinder.FindBestSuggestedHeader().Number;
            long headNumberOrZero = _blockFinder.Head?.Number ?? 0;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;
            
            return isSyncing;
        }
    }
}
