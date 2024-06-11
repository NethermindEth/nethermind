// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class ReorgTests
{
    private BlockchainProcessor _blockchainProcessor = null!;
    private BlockTree _blockTree = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        TrieStore trieStore = new(new MemDb(), LimboLogs.Instance);
        WorldState stateProvider = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
        StateReader stateReader = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        EthereumEcdsa ecdsa = new(1, LimboLogs.Instance);
        ITransactionComparerProvider transactionComparerProvider =
            new TransactionComparerProvider(specProvider, _blockTree);

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        TxPool.TxPool txPool = new(
            ecdsa,
            new BlobTxStorage(),
            new ChainHeadInfoProvider(specProvider, _blockTree, stateProvider),
            new TxPoolConfig(),
            new TxValidator(specProvider.ChainId),
            LimboLogs.Instance,
            transactionComparerProvider.GetDefaultComparer());
        BlockhashProvider blockhashProvider = new(_blockTree, specProvider, stateProvider, LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(
            blockhashProvider,
            specProvider,
            codeInfoRepository,
            LimboLogs.Instance);
        TransactionProcessor transactionProcessor = new(
            specProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance);

        BlockProcessor blockProcessor = new(
            MainnetSpecProvider.Instance,
            Always.Valid,
            new RewardCalculator(specProvider),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BlockhashStore(_blockTree, MainnetSpecProvider.Instance, stateProvider),
            LimboLogs.Instance);
        _blockchainProcessor = new BlockchainProcessor(
            _blockTree,
            blockProcessor,
            new RecoverSignatures(
                ecdsa,
                txPool,
                specProvider,
                LimboLogs.Instance),
            stateReader,
            LimboLogs.Instance, BlockchainProcessor.Options.Default);
    }

    [OneTimeTearDown]
    public void TearDown() => _blockchainProcessor?.Dispose();

    [Test, Timeout(Timeout.MaxTestTime)]
    [Retry(3)]
    public void Test()
    {
        List<Block> events = new();

        Block block0 = Build.A.Block.Genesis.WithDifficulty(1).WithTotalDifficulty(1L).TestObject;
        Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(2).WithTotalDifficulty(2L).TestObject;
        Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(1).WithTotalDifficulty(3L).TestObject;
        Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(3).WithTotalDifficulty(6L).TestObject;
        Block block1B = Build.A.Block.WithParent(block0).WithDifficulty(4).WithTotalDifficulty(5L).TestObject;
        Block block2B = Build.A.Block.WithParent(block1B).WithDifficulty(6).WithTotalDifficulty(11L).TestObject;

        _blockTree.BlockAddedToMain += (_, args) =>
        {
            events.Add(args.Block);
        };

        _blockchainProcessor.Start();

        _blockTree.SuggestBlock(block0);
        _blockTree.SuggestBlock(block1);
        _blockTree.SuggestBlock(block2);
        _blockTree.SuggestBlock(block3);
        _blockTree.SuggestBlock(block1B);
        _blockTree.SuggestBlock(block2B);

        Assert.That(() => _blockTree.Head, Is.EqualTo(block2B).After(10000, 500));

        events.Should().HaveCount(6);
        events[4].Hash.Should().Be(block1B.Hash!);
        events[5].Hash.Should().Be(block2B.Hash!);
    }
}
