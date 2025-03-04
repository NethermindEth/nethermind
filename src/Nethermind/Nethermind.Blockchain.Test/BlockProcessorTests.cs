// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Test;

public class BlockProcessorTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Prepared_block_contains_author_field()
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(
            HoleskySpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            transactionProcessor,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
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

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Recovers_state_on_cancel()
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(
            HoleskySpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            new RewardCalculator(MainnetSpecProvider.Instance),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            transactionProcessor,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
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

    [MaxTime(Timeout.MaxTestTime)]
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
        Address address = TestItem.Addresses[0];
        TestSingleReleaseSpecProvider spec = new TestSingleReleaseSpecProvider(ConstantinopleFix.Instance);
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(spec);
        testRpc.TestWallet.UnlockAccount(address, new SecureString());
        await testRpc.AddFunds(address, 1.Ether());
        await testRpc.AddBlock();
        SemaphoreSlim suggestedBlockResetEvent = new SemaphoreSlim(0);
        testRpc.BlockTree.NewHeadBlock += (_, _) =>
        {
            suggestedBlockResetEvent.Release(1);
        };

        int branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
        ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
        (await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10)).Should().BeTrue();
        Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
    }

    [TestCase(true)]
    [TestCase(false)]
    [MaxTime(Timeout.MaxTestTime)]
    public void Respects_externally_provided_checkpoint(bool updateCheckpoint)
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState state = new WorldState(trieStore, codeDb, LimboLogs.Instance);

        PrivateKey accountA = TestItem.PrivateKeyA;
        UInt256 nonceA = 2;
        state.CreateAccount(accountA.Address, new UInt256(1000_000_000), nonceA);
        state.RecalculateStateRoot();

        Hash256 afterA = state.StateRoot;

        PrivateKey accountB = TestItem.PrivateKeyB;
        state.CreateAccount(accountB.Address, new UInt256(10), 2);
        state.RecalculateStateRoot();

        Hash256 afterB = state.StateRoot;

        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(
            HoleskySpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, state),
            state,
            NullReceiptStorage.Instance,
            transactionProcessor,
            new BeaconBlockRootHandler(transactionProcessor, state),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance);

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(accountA)
            .To(accountB.Address)
            .WithNonce(nonceA)
            .WithValue(1000)
            .TestObject;

        Block block = Build.A.Block
            .WithTransactions(tx)
            .TestObject;

        // Checkpoint
        Hash256 expected;
        if (updateCheckpoint)
        {
            // Expect afterA
            expected = afterA;
            processor.UpdateCheckpoint(2, expected);
        }
        else
        {
            expected = afterB;
        }

        Block[] processedBlocks = processor.Process(afterA, [block],
            ProcessingOptions.EthereumMerge,
            NullBlockTracer.Instance);

        processedBlocks.Length.Should().Be(1);

        state.StateRoot.Should().Be(expected, "Because the state should be reverted back");
    }
}
