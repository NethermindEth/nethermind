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
using System.Drawing;
using System.Linq;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryManager : IFeeHistoryManager
        {
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(long blockCount, long lastBlockNumber, float[]? rewardPercentiles = null)
            {
                if (blockCount < 1)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail($"blockCount: Block count, {blockCount}, is less than 1.");
                }

                if (blockCount > 1024)
                {
                    blockCount = 1024;
                }

                if (rewardPercentiles != null)
                {
                    int index = 1;
                    int count = rewardPercentiles.Length;
                    int[] incorrectlySortedIndexes =
                        rewardPercentiles.Select(val => index).Where(val =>
                            index++ < count && rewardPercentiles[index] < rewardPercentiles[index - 1])
                            .ToArray();
                    if (incorrectlySortedIndexes.Any())
                    {
                        int firstIndex = incorrectlySortedIndexes.ElementAt(0);
                        return ResultWrapper<FeeHistoryResult>.Fail(
                            $"rewardPercentiles: Value at index {firstIndex}: {rewardPercentiles[firstIndex]} is less than " +
                            $"the value at previous index {firstIndex - 1}: {rewardPercentiles[firstIndex - 1]}.");
                    }
                }

                return FeeHistoryLookup();
            }

            private ResultWrapper<FeeHistoryResult> FeeHistoryLookup()
            {
                throw new NotImplementedException();
            }
        }
    }
}
