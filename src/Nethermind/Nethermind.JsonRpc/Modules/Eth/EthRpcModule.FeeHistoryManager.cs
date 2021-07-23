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

using System.Collections.Generic;
using Nethermind.Logging;
using Microsoft.VisualBasic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Int256;
using static Nethermind.JsonRpc.Modules.Eth.FeeHistoryResult;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryManager : IFeeHistoryManager
        {
            private readonly IBlockRangeManager _blockRangeManager;
            private IProcessBlockManager ProcessBlockManager { get; }
            private IInitialCheckManager InitialCheckManager { get; }

            private IFeeHistoryGenerator FeeHistoryGenerator { get; }

            public FeeHistoryManager(IBlockFinder blockFinder, ILogger logger, IBlockchainBridge blockchainBridge,
                IBlockRangeManager? blockRangeManager = null, IProcessBlockManager? processBlockManager = null,
                IInitialCheckManager? initialCheckManager = null, IFeeHistoryGenerator? feeHistoryGenerator = null)
            {
                _blockRangeManager = blockRangeManager ?? new BlockRangeManager(blockFinder);
                ProcessBlockManager = processBlockManager ?? new ProcessBlockManager(logger, blockchainBridge);
                InitialCheckManager = initialCheckManager ?? new InitialCheckManager();
                FeeHistoryGenerator = feeHistoryGenerator ?? new FeeHistoryGenerator(blockFinder, ProcessBlockManager);
            }

            public ResultWrapper<FeeHistoryResult> GetFeeHistory(ref long blockCount, long lastBlockNumber,
                double[]? rewardPercentiles = null)
            {
                ResultWrapper<FeeHistoryResult> initialCheckResult = InitialCheckManager.InitialChecksPassed(ref blockCount, rewardPercentiles);
                if (initialCheckResult.Result.ResultType == ResultType.Failure)
                {
                    return initialCheckResult;
                }
                
                long? headBlockNumber = null;
                ResultWrapper<BlockRangeInfo> blockRangeResult = _blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 
                    MaxHistory, ref headBlockNumber);
                if (blockRangeResult.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail(blockRangeResult.Result.Error ?? "Error message in ResolveBlockRange not set correctly.");
                }

                BlockRangeInfo blockRangeInfo = blockRangeResult.Data;
                long? oldestBlockNumber = blockRangeInfo.LastBlockNumber + 1 - blockRangeInfo.BlockCount;
                if (oldestBlockNumber == null)
                {
                    string output = StringOfNullElements(blockRangeInfo);
                    return ResultWrapper<FeeHistoryResult>.Fail($"{output} is null");
                }

                return FeeHistoryGenerator.FeeHistoryLookup(blockCount, lastBlockNumber, rewardPercentiles);
            }

            private static string StringOfNullElements(BlockRangeInfo blockRangeInfo)
            {
                List<string> nullStrings = new();
                if (blockRangeInfo.LastBlockNumber == null)
                    nullStrings.Add("LastBlockNumber");
                if (blockRangeInfo.BlockCount == null)
                    nullStrings.Add("BlockCount");
                string output = Strings.Join(nullStrings.ToArray(), ", ") ?? "";
                return output;
            }

            public class GasUsedAndReward
            {
                public long GasUsed { get; }
                public UInt256 Reward { get; }

                public GasUsedAndReward (long gasUsed, UInt256 reward)
                {
                    GasUsed = gasUsed;
                    Reward = reward;
                }
            }
        }
    }
}
