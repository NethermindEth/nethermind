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
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [Test]
        public void GetFeeHistory_IfInitialCheckResultFails_ReturnsError()
        {
            IInitialCheckManager initialCheckManager = Substitute.For<IInitialCheckManager>();
            ResultWrapper<FeeHistoryResult> expected = ResultWrapper<FeeHistoryResult>.Fail("Failed at Initial Check.");
            initialCheckManager.InitialChecksPassed(ref Arg.Any<long>(), Arg.Any<double[]>())
                .Returns(expected);
            FeeHistoryManager feeHistoryManager = GetSubstitutedFeeHistoryManager(initialCheckManager: initialCheckManager);
            long blockCount = 1;
            long lastBlockNumber = 3;

            ResultWrapper<FeeHistoryResult> resultWrapper = feeHistoryManager.GetFeeHistory(ref blockCount, lastBlockNumber);
                
            resultWrapper.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void GetFeeHistory_ResolveBlockRangeResultFails_ReturnsFailingWrapper()
        {
            IInitialCheckManager initialCheckManager = Substitute.For<IInitialCheckManager>();
            string expectedMessage = "Failed at ResolveBlockRange";
            ResultWrapper<FeeHistoryResult> expected = ResultWrapper<FeeHistoryResult>.Fail(expectedMessage);
            initialCheckManager.InitialChecksPassed(ref Arg.Any<long>(), Arg.Any<double[]>())
                .Returns(ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult()));
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            blockRangeManager
                .ResolveBlockRange(ref Arg.Any<long>(), ref Arg.Any<long>(), Arg.Any<int>(), ref Arg.Any<long?>())
                .Returns(ResultWrapper<BlockRangeInfo>.Fail(expectedMessage));
            FeeHistoryManager feeHistoryManager = GetSubstitutedFeeHistoryManager(initialCheckManager: initialCheckManager, blockRangeManager: blockRangeManager);
            long blockCount = 1;
            long lastBlockNumber = 3;

            ResultWrapper<FeeHistoryResult>
                resultWrapper = feeHistoryManager.GetFeeHistory(ref blockCount, lastBlockNumber);
            
            resultWrapper.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void GetFeeHistory_OldestBlockNumberIsNull_ReturnsFailingWrapper()
        {
            IInitialCheckManager initialCheckManager = Substitute.For<IInitialCheckManager>();
            ResultWrapper<FeeHistoryResult> expected = ResultWrapper<FeeHistoryResult>.Fail("LastBlockNumber, BlockCount is null");
            initialCheckManager.InitialChecksPassed(ref Arg.Any<long>(), Arg.Any<double[]>())
                .Returns(ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult()));
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            blockRangeManager
                .ResolveBlockRange(ref Arg.Any<long>(), ref Arg.Any<long>(), Arg.Any<int>(), ref Arg.Any<long?>())
                .Returns(ResultWrapper<BlockRangeInfo>.Success(new BlockRangeInfo {LastBlockNumber = null, BlockCount = null}));
            FeeHistoryManager feeHistoryManager = GetSubstitutedFeeHistoryManager(initialCheckManager: initialCheckManager, blockRangeManager: blockRangeManager);
            long blockCount = 1;
            long lastBlockNumber = 3;

            ResultWrapper<FeeHistoryResult>
                resultWrapper = feeHistoryManager.GetFeeHistory(ref blockCount, lastBlockNumber);
            
            resultWrapper.Should().BeEquivalentTo(expected);
            
        }
        public FeeHistoryManager GetSubstitutedFeeHistoryManager(
            IBlockFinder? blockFinder = null, 
            ILogger? logger = null,
            IBlockRangeManager? blockRangeManager = null,
            IProcessBlockManager? processBlockManager = null,
            IInitialCheckManager? initialCheckManager = null,
            IFeeHistoryGenerator? feeHistoryGenerator = null)
        {
            
            return new(
                blockFinder ?? Substitute.For<IBlockFinder>(),
                logger ?? Substitute.For<ILogger>(),
                blockRangeManager ?? Substitute.For<IBlockRangeManager>(),
                processBlockManager ?? Substitute.For<IProcessBlockManager>(),
                initialCheckManager ?? Substitute.For<IInitialCheckManager>(),
                feeHistoryGenerator ?? Substitute.For<IFeeHistoryGenerator>());
        }
    }
}
