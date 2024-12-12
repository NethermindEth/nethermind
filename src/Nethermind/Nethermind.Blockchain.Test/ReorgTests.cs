// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class ReorgTests
{
    private BlockchainProcessor _blockchainProcessor = null!;
    private BlockTree _blockTree = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        TrieStore trieStore = new(new MemDb(), Prune.WhenCacheReaches(1).KeepingLastNState(1), No.Persistence, LimboLogs.Instance);
        WorldState stateProvider = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
        StateReader stateReader = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
        ISpecProvider specProvider = new OverridableSpecProvider(MainnetSpecProvider.Instance,
            (spec, forkActivation) => new OverridableReleaseSpec(spec) { BlockReward = (UInt256)(forkActivation.BlockNumber * 1_000_000) });
        EthereumEcdsa ecdsa = new(1);
        ITransactionComparerProvider transactionComparerProvider =
            new TransactionComparerProvider(specProvider, _blockTree);

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        CodeInfoRepository codeInfoRepository = new();
        TxPool.TxPool txPool = new(
            ecdsa,
            new BlobTxStorage(),
            new ChainHeadInfoProvider(specProvider, _blockTree, stateProvider, codeInfoRepository),
            new TxPoolConfig(),
            new TxValidator(specProvider.ChainId),
            LimboLogs.Instance,
            transactionComparerProvider.GetDefaultComparer());
        BlockhashProvider blockhashProvider = new(_blockTree, specProvider, stateProvider, LimboLogs.Instance);
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
            new MyValidator(),
            new RewardCalculator(specProvider),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            transactionProcessor,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            new BlockhashStore(MainnetSpecProvider.Instance, stateProvider),
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

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Retry(3)]
    public void Test()
    {
        List<Block> events = new();

        Block block0 = Build.A.Block.Genesis.WithDifficulty(1).WithTotalDifficulty(1L).WithStateRoot(new Hash256("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421")).TestObject;
        Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(2).WithTotalDifficulty(2L).WithStateRoot(new Hash256("0x8ccdf2379bbcc6e7d3e67b0069cb36631eac79e9ba5d37ed044933a27cac5089")).TestObject;
        Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(1).WithTotalDifficulty(3L).WithStateRoot(new Hash256("0x8ccdf2379bbcc6e7d3e67b0069cb36631eac79e9ba5d37ed044933a27cac5089")).TestObject;
        Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(3).WithTotalDifficulty(6L).WithStateRoot(new Hash256("0x8ccdf2379bbcc6e7d3e67b0069cb36631eac79e9ba5d37ed044933a27cac5089")).TestObject;
        Block block1B = Build.A.Block.WithParent(block0).WithDifficulty(4).WithTotalDifficulty(5L).WithStateRoot(new Hash256("0x8ccdf2379bbcc6e7d3e67b0069cb36631eac79e9ba5d37ed044933a27cac5089")).TestObject;
        Block block2B = Build.A.Block.WithParent(block1B).WithDifficulty(6).WithTotalDifficulty(11L).WithStateRoot(new Hash256("0xe893ec1c6ac86b586396136fba166e88630b767415f5b25ea47e404dea88cec8")).TestObject;

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

        // Assert.That(() => _blockTree.Head, Is.EqualTo(block2B).After(10000, 500));
        //
        Assert.That(() => events.Count, Is.EqualTo(6).After(10000, 500));
        // events.Should().HaveCount(6);
        // events[4].Hash.Should().Be(block1B.Hash!);
        // events[5].Hash.Should().Be(block2B.Hash!);
    }
}

public class MyValidator : IBlockValidator
{
    IBlockValidator _inner = Always.Valid;

    public MyValidator()
    {

    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        return _inner.Validate(header, parent, isUncle, out error);
    }

    public bool Validate(BlockHeader header, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        return _inner.Validate(header, isUncle, out error);
    }

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        return _inner.ValidateWithdrawals(block, out error);
    }

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error)
    {
        return _inner.ValidateOrphanedBlock(block, out error);
    }

    public bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true)
    {
        return _inner.ValidateSuggestedBlock(block, out error, validateHashes);
    }

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        Console.WriteLine($"{processedBlock.Number} state {processedBlock.StateRoot}");
        return _inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);
    }
}
