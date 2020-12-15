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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Blockchain;
using System.Security;
using Nethermind.Core.Extensions;
using Nethermind.Specs.Forks;
using Nethermind.JsonRpc.Test.Modules;
using System.Threading.Tasks;
using System.Threading;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockProcessorTests
    {
        [Test]
        public void Prepared_block_contains_author_field()
        {
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            BlockProcessor processor = new BlockProcessor(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                transactionProcessor,
                stateDb,
                codeDb,
                stateProvider,
                new StorageProvider(stateDb, stateProvider, LimboLogs.Instance),
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            Block[] processedBlocks = processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                NullBlockTracer.Instance);
            Assert.AreEqual(1, processedBlocks.Length, "length");
            Assert.AreEqual(block.Author, processedBlocks[0].Author, "author");
        }

        [Test]
        public void Recovers_state_on_cancel()
        {
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            BlockProcessor processor = new BlockProcessor(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                new RewardCalculator(MainnetSpecProvider.Instance),
                transactionProcessor,
                stateDb,
                codeDb,
                stateProvider,
                new StorageProvider(stateDb, stateProvider, LimboLogs.Instance),
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithTransactions(1).WithHeader(header).TestObject;
            Assert.Throws<OperationCanceledException>(() => processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                AlwaysCancelBlockTracer.Instance));

            Assert.Throws<OperationCanceledException>(() => processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                AlwaysCancelBlockTracer.Instance));
        }

        [Test]
        public async Task Process_long_running_branch()
        {
            var address = TestItem.Addresses[0];
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            var blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000000000);
            var testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .WithGenesisBlockBuilder(blockBuilder)
                .Build(spec);
            testRpc.TestWallet.UnlockAccount(address, new SecureString());
            await testRpc.AddFunds(address, 1.Ether());

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            //List<Block> blocks = new List<Block>();
            //for (int i=0; i < 3000; ++i)
            //{
            //    blocks.Add(block);
            //}
            //testRpc.BlockProcessor.Process(TestItem.KeccakA, blocks, ProcessingOptions.None, NullBlockTracer.Instance);
            await testRpc.AddBlock();
            var _suggestedBlockResetEvent = new SemaphoreSlim(0);
            testRpc.BlockTree.NewHeadBlock += (s, e) =>
            {
                _suggestedBlockResetEvent.Release(1);
            };

 //           testRpc.BlockchainProcessor.PauseProcessingLoop();
            ((BlockTree)testRpc.BlockTree).AddBranch(2006, 5, 0);
  //          testRpc.BlockchainProcessor.ResumeProcessingLoop();
            await _suggestedBlockResetEvent.WaitAsync();
        }
    }
}
