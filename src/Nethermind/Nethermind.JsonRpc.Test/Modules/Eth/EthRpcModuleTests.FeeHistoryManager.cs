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

#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {


        class TestableFeeHistoryManager : FeeHistoryManager
        {
            public List<BlockFeeInfo>? BlockFeeInfos { get; private set; }

            public TestableFeeHistoryManager(
                IBlockFinder blockFinder,
                ILogger logger,
                IBlockRangeManager? blockRangeManager = null) : 
                base(blockFinder,
                    logger,
                    blockRangeManager)
            {
                BlockFeeInfos = null;
            }

            protected virtual ResultWrapper<FeeHistoryResult> SuccessfulResult(long blockCount, List<BlockFeeInfo> blockFeeInfos)
            {
                BlockFeeInfos = blockFeeInfos;
                return ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult());
            }

            protected internal override BlockFeeInfo GetBlockFeeInfo(long blockNumber, double[]? rewardPercentiles,
                Block? pendingBlock)
            {
                BlockFeeInfo blockFeeInfo = new() {BlockNumber = blockNumber};
                return blockFeeInfo;
            }

        }
    }
}
