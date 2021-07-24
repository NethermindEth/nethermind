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
using Nethermind.Core;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.LastBlockNumberConsts;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class BlockRangeManager : IBlockRangeManager
    {
        private readonly IBlockFinder _blockFinder;

        public BlockRangeManager(IBlockFinder blockFinder)
        {
            _blockFinder = blockFinder;
        }

        public ResultWrapper<BlockRangeInfo> ResolveBlockRange(ref long lastBlockNumber, ref long blockCount, int maxHistory, ref long? headBlockNumber)
        {
            Block? pendingBlock = null;
            if (lastBlockNumber == PendingBlockNumber)
            {
                ResultWrapper<BlockRangeInfo> checkPendingBlockNumber = PendingBlockNumberCheck(ref lastBlockNumber, ref blockCount, ref pendingBlock, ref headBlockNumber);
                if (checkPendingBlockNumber.Result.ResultType == ResultType.Failure)
                {
                    return checkPendingBlockNumber;
                }
            }

            if (pendingBlock == null)
            {
                headBlockNumber = _blockFinder.FindHeadBlock()?.Number;
                (bool returnEarly, ResultWrapper<BlockRangeInfo> fail) = HeadBlockNumberRelatedErrors(lastBlockNumber, headBlockNumber);
                if (returnEarly)
                {
                    return fail;
                }
            }
            if (lastBlockNumber == LatestBlockNumber)
            {
                lastBlockNumber = (long) headBlockNumber!;
            }
            
            if (maxHistory != 0)
            {
                ResultWrapper<long> resultWrapper = CalculateTooOldCount(lastBlockNumber, blockCount, maxHistory, headBlockNumber);
                if (resultWrapper.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<BlockRangeInfo>.Fail(resultWrapper.Result.Error!);
                }

                blockCount -= resultWrapper.Data;
            }
            if (blockCount > lastBlockNumber + 1)
            {
                blockCount = lastBlockNumber + 1;
            }
            return ResultWrapper<BlockRangeInfo>.Success(new BlockRangeInfo(pendingBlock: pendingBlock, blockCount: blockCount, headBlockNumber: headBlockNumber, lastBlockNumber: lastBlockNumber));
        }

        private static (bool returnEarly, ResultWrapper<BlockRangeInfo>) HeadBlockNumberRelatedErrors(long lastBlockNumber, long? headBlockNumber)
        {
            if (headBlockNumber == null)
            {
                return (true, ResultWrapper<BlockRangeInfo>.Fail("Head block not found."));
            }

            if (lastBlockNumber > headBlockNumber)
            {
                {
                    return (true, ResultWrapper<BlockRangeInfo>.Fail(
                        "Pending block not present and last block number greater than head number."));
                }
            }

            return (false, ResultWrapper<BlockRangeInfo>.Success(new BlockRangeInfo()));
        }

        private ResultWrapper<BlockRangeInfo> PendingBlockNumberCheck(ref long lastBlockNumber, ref long blockCount, ref Block? pendingBlock, ref long? headBlockNumber)
        {
            pendingBlock = _blockFinder.FindPendingBlock();
            if (pendingBlock != null)
            {
                lastBlockNumber = pendingBlock.Number;
                headBlockNumber = pendingBlock.Number - 1;
            }
            else
            {
                lastBlockNumber = LatestBlockNumber;
                blockCount--;
                if (blockCount == 0)
                {
                    return ResultWrapper<BlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0.");
                }
            }

            return ResultWrapper<BlockRangeInfo>.Success(new BlockRangeInfo());
        }

        public virtual ResultWrapper<long> CalculateTooOldCount(long lastBlockNumber, long blockCount, int maxHistory,
            long? headBlockNumber)
        {
            long tooOldCount = (long) headBlockNumber! - maxHistory - lastBlockNumber - blockCount;
            if (blockCount > tooOldCount)
            {
                return ResultWrapper<long>.Success(tooOldCount);
            }
            else
            {
                return ResultWrapper<long>.Fail($"Block count: {blockCount}, is less than old blocks to remove: {tooOldCount}.");
            }
        }
    }
}
