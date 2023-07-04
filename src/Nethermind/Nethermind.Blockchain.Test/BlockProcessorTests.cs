// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using System.Security;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Test.Modules;
using System.Threading.Tasks;
using System.Threading;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockProcessorTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Prepared_block_contains_author_field()
        {
            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();
            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            BlockProcessor processor = new(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            Block[] processedBlocks = processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> { block },
                ProcessingOptions.None,
                NullBlockTracer.Instance);
            Assert.That(processedBlocks.Length, Is.EqualTo(1), "length");
            Assert.That(processedBlocks[0].Author, Is.EqualTo(block.Author), "author");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_store_a_witness()
        {
            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();
            var trieStore = new TrieStore(stateDb, LimboLogs.Instance);

            IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            IWitnessCollector witnessCollector = Substitute.For<IWitnessCollector>();
            BlockProcessor processor = new(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                NullReceiptStorage.Instance,
                witnessCollector,
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            _ = processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> { block },
                ProcessingOptions.None,
                NullBlockTracer.Instance);

            witnessCollector.Received(1).Persist(block.Hash);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Recovers_state_on_cancel()
        {
            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();
            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            BlockProcessor processor = new(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                new RewardCalculator(MainnetSpecProvider.Instance),
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithTransactions(1, MuirGlacier.Instance).WithHeader(header).TestObject;
            Assert.Throws<OperationCanceledException>(() => processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> { block },
                ProcessingOptions.None,
                AlwaysCancelBlockTracer.Instance));

            Assert.Throws<OperationCanceledException>(() => processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> { block },
                ProcessingOptions.None,
                AlwaysCancelBlockTracer.Instance));
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(20)]
        [TestCase(63)]
        [TestCase(64)]
        [TestCase(65)]
        [TestCase(127)]
        [TestCase(128)]
        [TestCase(129)]
        [TestCase(130)]
        [TestCase(1000)]
        [TestCase(2000)]
        public async Task Process_long_running_branch(int blocksAmount)
        {
            var address = TestItem.Addresses[0];
            var spec = new TestSingleReleaseSpecProvider(ConstantinopleFix.Instance);
            var testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(spec);
            testRpc.TestWallet.UnlockAccount(address, new SecureString());
            await testRpc.AddFunds(address, 1.Ether());
            await testRpc.AddBlock();
            var suggestedBlockResetEvent = new SemaphoreSlim(0);
            testRpc.BlockTree.NewHeadBlock += (s, e) =>
            {
                suggestedBlockResetEvent.Release(1);
            };

            var branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
            ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
            (await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10)).Should().BeTrue();
            Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
        }
    }
}
