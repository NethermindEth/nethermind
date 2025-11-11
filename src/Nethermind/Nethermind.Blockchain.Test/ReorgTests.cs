// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ReorgTests
{
    private BlockchainProcessor _blockchainProcessor = null!;
    private BlockTree _blockTree = null!;
    private BlockHeader _genesis = null!;
    private BlockhashProvider _blockhashProvider = null!;
    private bool _started;

    // EVM/state dependencies for executing BLOCKHASH inside tx
    private IWorldState _worldState = null!;
    private ISpecProvider _specProvider = null!;
    private EthereumEcdsa _ecdsa = null!;
    private Address _contractAddress = null!;
    private Address _sender = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        (IWorldState stateProvider, IStateReader stateReader) = TestWorldStateFactory.CreateForTestWithStateReader(memDbProvider, LimboLogs.Instance);
        _worldState = stateProvider;

        IReleaseSpec finalSpec = _specProvider.GetFinalSpec();

        using (var _ = stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            if (finalSpec.WithdrawalsEnabled)
            {
                stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
                stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, _specProvider.GenesisSpec);
            }

            if (finalSpec.ConsolidationRequestsEnabled)
            {
                stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
                stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, _specProvider.GenesisSpec);
            }

            // Predeploy: fund sender and install a contract that executes BLOCKHASH(NUMBER-1) and stores it at storage[1]
            _sender = TestItem.AddressA;
            stateProvider.CreateAccount(_sender, 10.Ether());

            _contractAddress = TestItem.AddressB;
            stateProvider.CreateAccount(_contractAddress, 0);

            byte[] blockhashStoreCode = Prepare.EvmCode
                .Op(Instruction.NUMBER)
                .Op(Instruction.DUP1)
                .PushData(1)
                .Op(Instruction.SUB)
                .Op(Instruction.BLOCKHASH)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SSTORE)
                .Done;
            stateProvider.InsertCode(_contractAddress, blockhashStoreCode, _specProvider.GenesisSpec);

            stateProvider.Commit(_specProvider.GenesisSpec);
            stateProvider.CommitTree(0);

            _genesis = Build.A.BlockHeader.WithStateRoot(stateProvider.StateRoot).TestObject;
        }

        _ecdsa = new EthereumEcdsa(1);
        ITransactionComparerProvider transactionComparerProvider =
            new TransactionComparerProvider(_specProvider, _blockTree);

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(_specProvider)
            .TestObject;

        TxPool.TxPool txPool = new(
            _ecdsa,
            new BlobTxStorage(),
            new ChainHeadInfoProvider(
                new ChainHeadSpecProvider(_specProvider, _blockTree), _blockTree, stateReader),
            new TxPoolConfig(),
            new TxValidator(_specProvider.ChainId),
            LimboLogs.Instance,
            transactionComparerProvider.GetDefaultComparer());
        BlockhashProvider blockhashProvider = new(_blockTree, _specProvider, stateProvider, LimboLogs.Instance);
        _blockhashProvider = new BlockhashProvider(_blockTree, _specProvider, stateProvider, LimboLogs.Instance);
        VirtualMachine virtualMachine = new(
            blockhashProvider,
            _specProvider,
            LimboLogs.Instance);
        TransactionProcessor transactionProcessor = new(
            BlobBaseFeeCalculator.Instance,
            _specProvider,
            stateProvider,
            virtualMachine,
            new EthereumCodeInfoRepository(stateProvider),
            LimboLogs.Instance);

        BlockProcessor blockProcessor = new BlockProcessor(
            MainnetSpecProvider.Instance,
            Always.Valid,
            new RewardCalculator(_specProvider),
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            new BlockhashStore(MainnetSpecProvider.Instance, stateProvider),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));
        BranchProcessor branchProcessor = new BranchProcessor(
            blockProcessor,
            MainnetSpecProvider.Instance,
            stateProvider,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            LimboLogs.Instance);

        _blockchainProcessor = new BlockchainProcessor(
            _blockTree,
            branchProcessor,
            new RecoverSignatures(
                _ecdsa,
                _specProvider,
                LimboLogs.Instance),
            stateReader,
            LimboLogs.Instance, BlockchainProcessor.Options.Default);
    }

    private void EnsureStarted()
    {
        if (!_started)
        {
            _blockchainProcessor.Start();
            _started = true;
        }
    }

    [TearDown]
    public async Task TearDownAsync() => await (_blockchainProcessor?.DisposeAsync() ?? default);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockTree_reorg_updates_head_and_emits_events_in_order()
    {
        EnsureStarted();
        List<Block> events = new();

        Block block0 = Build.A.Block.WithHeader(_genesis).WithDifficulty(1).WithTotalDifficulty(1L).TestObject;
        Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(2).WithTotalDifficulty(2L).TestObject;
        Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(1).WithTotalDifficulty(3L).TestObject;
        Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(3).WithTotalDifficulty(6L).TestObject;
        Block block1B = Build.A.Block.WithParent(block0).WithDifficulty(4).WithTotalDifficulty(5L).TestObject;
        Block block2B = Build.A.Block.WithParent(block1B).WithDifficulty(6).WithTotalDifficulty(11L).TestObject;

        int count = 0;
        ManualResetEventSlim mre = new(false);
        _blockTree.BlockAddedToMain += (_, args) =>
        {
            events.Add(args.Block);
            count++;
            if (count >= 6)
            {
                mre.Set();
            }
        };

        _blockTree.SuggestBlock(block0);
        _blockTree.SuggestBlock(block1);
        _blockTree.SuggestBlock(block2);
        _blockTree.SuggestBlock(block3);
        _blockTree.SuggestBlock(block1B);
        _blockTree.SuggestBlock(block2B);

        // Wait until all events are received
        mre.Wait(Timeout.MaxTestTime);
        Assert.That(_blockTree.Head, Is.EqualTo(block2B));

        events.Should().HaveCount(6);
        events[4].Hash.Should().Be(block1B.Hash!);
        events[5].Hash.Should().Be(block2B.Hash!);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_provider_correct_on_multi_block_reorg_via_blockchain_processor()
    {
        EnsureStarted();

        // Build initial canonical chain: genesis (0) -> 1 -> 2 -> 3 -> 4 (total diff small)
        Block block0 = Build.A.Block.WithHeader(_genesis).WithDifficulty(1).WithTotalDifficulty(1L).TestObject; // number 0
        Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(1).WithTotalDifficulty(2L).TestObject;    // number 1
        Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(1).WithTotalDifficulty(3L).TestObject;    // number 2
        Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(1).WithTotalDifficulty(4L).TestObject;    // number 3
        Block block4 = Build.A.Block.WithParent(block3).WithDifficulty(1).WithTotalDifficulty(5L).TestObject;    // number 4
        
        int count = 0;
        ManualResetEventSlim mre = new(false);
        _blockTree.BlockAddedToMain += (_, args) =>
        {
            count++;
            if (count == 5 || count == 8)
            {
                mre.Set();
            }
        };

        _blockTree.SuggestBlock(block0);
        _blockTree.SuggestBlock(block1);
        _blockTree.SuggestBlock(block2);
        _blockTree.SuggestBlock(block3);
        _blockTree.SuggestBlock(block4);
        
        // Wait until all events are received
        mre.Wait(Timeout.MaxTestTime);
        Assert.That(_blockTree.Head, Is.EqualTo(block4));

        // Build heavier alternative branch from block1 (replaces 2,3,4)
        Block block2B = Build.A.Block.WithParent(block1).WithDifficulty(5).WithTotalDifficulty(7L).TestObject;    // number 2
        Block block3B = Build.A.Block.WithParent(block2B).WithDifficulty(6).WithTotalDifficulty(13L).TestObject;  // number 3
        Block block4B = Build.A.Block.WithParent(block3B).WithDifficulty(7).WithTotalDifficulty(20L).TestObject;  // number 4 (heavier than canonical 5)

        mre.Reset();
        // Suggest alt branch blocks
        _blockTree.SuggestBlock(block2B);
        _blockTree.SuggestBlock(block3B);
        _blockTree.SuggestBlock(block4B);

        // Wait until head moves to block4B (reorg occurred)
        mre.Wait(Timeout.MaxTestTime);
        Assert.That(_blockTree.Head, Is.EqualTo(block4B));

        // Create a new current block (number 5) atop the new head without suggesting it yet
        Block current = Build.A.Block.WithParent(block4B).WithDifficulty(1).WithTotalDifficulty(21L).TestObject; // number 5

        // Verify blockhash lookups for replaced and preserved blocks
        // Self lookup (current.Number) should be null
        _blockhashProvider.GetBlockhash(current.Header, current.Number).Should().BeNull();

        // Future lookup should be null
        _blockhashProvider.GetBlockhash(current.Header, current.Number + 1).Should().BeNull();

        // Parent (fast path)
        _blockhashProvider.GetBlockhash(current.Header, block4B.Number).Should().Be(block4B.Hash!);

        // Replaced branch numbers (2,3,4) should return alt branch hashes
        _blockhashProvider.GetBlockhash(current.Header, block4B.Number - 1).Should().Be(block3B.Hash!); // 3
        _blockhashProvider.GetBlockhash(current.Header, block4B.Number - 2).Should().Be(block2B.Hash!); // 2

        // Unreplaced earlier block (1) should still return original hash
        _blockhashProvider.GetBlockhash(current.Header, block1.Number).Should().Be(block1.Hash!);

        // Ensure original canonical hashes differ from the alt branch ones to validate reorg effect
        block2B.Hash.Should().NotBe(block2.Hash!);
        block3B.Hash.Should().NotBe(block3.Hash!);
        block4B.Hash.Should().NotBe(block4.Hash!);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_opcode_in_tx_returns_parent_hash_on_alt_branch()
    {
        EnsureStarted();

        // Helper to create tx with given nonce that calls contract
        Transaction MakeTx(ulong nonce) => Build.A.Transaction
            .WithNonce(nonce)
            .WithTo(_contractAddress)
            .WithGasLimit(100_000)
            .WithValue(0)
            .WithGasPrice(1)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA)
            .TestObject;

        // Build initial canonical chain with tx in every block
        Block b0 = Build.A.Block.WithHeader(_genesis).WithDifficulty(1).WithTotalDifficulty(1L).WithGasLimit(30_000_000).TestObject;
        Block b1 = Build.A.Block.WithParent(b0).WithDifficulty(1).WithTotalDifficulty(2L).WithGasLimit(30_000_000).WithTransactions(MakeTx(0)).TestObject;
        Block b2 = Build.A.Block.WithParent(b1).WithDifficulty(1).WithTotalDifficulty(3L).WithGasLimit(30_000_000).WithTransactions(MakeTx(1)).TestObject;
        Block b3 = Build.A.Block.WithParent(b2).WithDifficulty(1).WithTotalDifficulty(4L).WithGasLimit(30_000_000).WithTransactions(MakeTx(2)).TestObject;

        int count = 0;
        ManualResetEventSlim mre = new(false);
        _blockchainProcessor.InvalidBlock += (_, args) =>
        {
            Console.WriteLine($"Invalid block detected: {args.InvalidBlock.Number}");
        };
        _blockchainProcessor.BlockRemoved += (_, args) =>
        { 
            Console.WriteLine($"Block removed from main: {args.BlockHash}, {args.ProcessingResult}, {args.Message}, {args.Exception}");
        };
        _blockTree.BlockAddedToMain += (_, args) =>
        {
            count++;
            Console.WriteLine($"Block added to main: {args.Block.Number} (count={count})");
            if (count == 4 || count == 8)
            {
                mre.Set();
            }
        };

        _blockTree.SuggestBlock(b0);
        _blockTree.SuggestBlock(b1);
        _blockTree.SuggestBlock(b2);
        _blockTree.SuggestBlock(b3);
        mre.Wait(Timeout.MaxTestTime);
        Assert.That(_blockTree.Head, Is.EqualTo(b3));

        // Verify canonical chain storage: each block N should have parent hash at storage[N]
        _worldState.Get(new StorageCell(_contractAddress, 1)).ToArray().Should().BeEquivalentTo(b0.Hash!.BytesToArray());
        _worldState.Get(new StorageCell(_contractAddress, 2)).ToArray().Should().BeEquivalentTo(b1.Hash!.BytesToArray());
        _worldState.Get(new StorageCell(_contractAddress, 3)).ToArray().Should().BeEquivalentTo(b2.Hash!.BytesToArray());

        // Build heavier alternative branch from b1 with tx in every block
        Block b2B = Build.A.Block.WithParent(b1).WithDifficulty(5).WithTotalDifficulty(7L).WithGasLimit(30_000_000).WithTransactions(MakeTx(1)).TestObject;
        Block b3B = Build.A.Block.WithParent(b2B).WithDifficulty(6).WithTotalDifficulty(13L).WithGasLimit(30_000_000).WithTransactions(MakeTx(2)).TestObject;
        Block b4B = Build.A.Block.WithParent(b3B).WithDifficulty(7).WithTotalDifficulty(20L).WithGasLimit(30_000_000).WithTransactions(MakeTx(3)).TestObject;
        Block b5B = Build.A.Block.WithParent(b4B).WithDifficulty(8).WithTotalDifficulty(28L).WithGasLimit(30_000_000).WithTransactions(MakeTx(4)).TestObject;

        mre.Reset();
        _blockTree.SuggestBlock(b2B);
        _blockTree.SuggestBlock(b3B);
        _blockTree.SuggestBlock(b4B);
        _blockTree.SuggestBlock(b5B);
        mre.Wait(Timeout.MaxTestTime);
        Assert.That(_blockTree.Head, Is.EqualTo(b5B));

        // Verify reorg branch storage: after reorg, storage should reflect new canonical chain
        // storage[1] still has b0 hash (unchanged)
        _worldState.Get(new StorageCell(_contractAddress, 1)).ToArray().Should().BeEquivalentTo(b0.Hash!.BytesToArray());
        // storage[2] now has b1 hash (from b2B execution)
        _worldState.Get(new StorageCell(_contractAddress, 2)).ToArray().Should().BeEquivalentTo(b1.Hash!.BytesToArray());
        // storage[3] now has b2B hash (from b3B execution)
        _worldState.Get(new StorageCell(_contractAddress, 3)).ToArray().Should().BeEquivalentTo(b2B.Hash!.BytesToArray());
        // storage[4] now has b3B hash (from b4B execution)
        _worldState.Get(new StorageCell(_contractAddress, 4)).ToArray().Should().BeEquivalentTo(b3B.Hash!.BytesToArray());
        // storage[5] now has b4B hash (from b5B execution)
        _worldState.Get(new StorageCell(_contractAddress, 5)).ToArray().Should().BeEquivalentTo(b4B.Hash!.BytesToArray());
    }
}
