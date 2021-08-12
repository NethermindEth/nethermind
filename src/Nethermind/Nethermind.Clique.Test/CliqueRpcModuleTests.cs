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

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class CliqueRpcModuleTests
    {
        [Test]
        public void Sets_clique_block_producer_properly()
        {
            CliqueConfig cliqueConfig = new CliqueConfig();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Signer signer = new Signer(ChainId.Ropsten, TestItem.PrivateKeyA, LimboLogs.Instance);
            CliqueBlockProducer producer = new CliqueBlockProducer(
                Substitute.For<ITxSource>(),
                Substitute.For<IBlockchainProcessor>(),
                Substitute.For<IStateProvider>(),
                blockTree,
                Substitute.For<ITimestamper>(),
                Substitute.For<ICryptoRandom>(),
                Substitute.For<ISnapshotManager>(),
                new CliqueSealer(signer, cliqueConfig, Substitute.For<ISnapshotManager>(), LimboLogs.Instance),
                new TargetAdjustedGasLimitCalculator(GoerliSpecProvider.Instance, new MiningConfig()),
                MainnetSpecProvider.Instance, 
                cliqueConfig,
                LimboLogs.Instance);
            
            SnapshotManager snapshotManager = new SnapshotManager(CliqueConfig.Default, new MemDb(), Substitute.For<IBlockTree>(), NullEthereumEcdsa.Instance, LimboLogs.Instance);
            
            CliqueRpcRpcModule bridge = new CliqueRpcRpcModule(producer, snapshotManager, blockTree);
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, true));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, false));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
        }
        
        [Test]
        public void Can_ask_for_block_signer()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockHeader header = Build.A.BlockHeader.TestObject;
            blockFinder.FindHeader(Arg.Any<Keccak>()).Returns(header);
            snapshotManager.GetBlockSealer(header).Returns(TestItem.AddressA);
            CliqueRpcRpcModule rpcModule = new CliqueRpcRpcModule(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType.Should().Be(ResultType.Success);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Data.Should().Be(TestItem.AddressA);
        }
        
        [Test]
        public void Can_ask_for_block_signer_when_block_is_unknown()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeader(Arg.Any<Keccak>()).Returns((BlockHeader)null);
            CliqueRpcRpcModule rpcModule = new CliqueRpcRpcModule(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType.Should().Be(ResultType.Failure);
        }
        
        [Test]
        public void Can_ask_for_block_signer_when_hash_is_null()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            CliqueRpcRpcModule rpcModule = new CliqueRpcRpcModule(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(null).Result.ResultType.Should().Be(ResultType.Failure);
        }
    }
}
