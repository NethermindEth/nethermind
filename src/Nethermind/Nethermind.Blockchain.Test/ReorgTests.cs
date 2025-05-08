// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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

public class ReorgTests
{
#pragma warning disable NUnit1032 // An IDisposable field/property should be Disposed in a TearDown method
    private BlockchainProcessor _blockchainProcessor = null!;
#pragma warning restore NUnit1032 // An IDisposable field/property should be Disposed in a TearDown method
    private BlockTree _blockTree = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        TrieStore trieStore = new(new MemDb(), LimboLogs.Instance);
        WorldState stateProvider = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);

        if (specProvider?.GetFinalSpec().WithdrawalsEnabled is true)
        {
            var code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe1460cb5760115f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff146101f457600182026001905f5b5f82111560685781019083028483029004916001019190604d565b909390049250505036603814608857366101f457346101f4575f5260205ff35b34106101f457600154600101600155600354806003026004013381556001015f35815560010160203590553360601b5f5260385f601437604c5fa0600101600355005b6003546002548082038060101160df575060105b5f5b8181146101835782810160030260040181604c02815460601b8152601401816001015481526020019060020154807fffffffffffffffffffffffffffffffff00000000000000000000000000000000168252906010019060401c908160381c81600701538160301c81600601538160281c81600501538160201c81600401538160181c81600301538160101c81600201538160081c81600101535360010160e1565b910180921461019557906002556101a0565b90505f6002555f6003555b5f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff14156101cd57505f5b6001546002828201116101e25750505f6101e8565b01600290035b5f555f600155604c025ff35b5f5ffd");
            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, 1);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, code, specProvider.GenesisSpec);
        }

        if (specProvider?.GetFinalSpec().ConsolidationRequestsEnabled is true)
        {
            var code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe1460d35760115f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff1461019a57600182026001905f5b5f82111560685781019083028483029004916001019190604d565b9093900492505050366060146088573661019a573461019a575f5260205ff35b341061019a57600154600101600155600354806004026004013381556001015f358155600101602035815560010160403590553360601b5f5260605f60143760745fa0600101600355005b6003546002548082038060021160e7575060025b5f5b8181146101295782810160040260040181607402815460601b815260140181600101548152602001816002015481526020019060030154905260010160e9565b910180921461013b5790600255610146565b90505f6002555f6003555b5f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff141561017357505f5b6001546001828201116101885750505f61018e565b01600190035b5f555f6001556074025ff35b5f5ffd");
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, 1);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, code, specProvider.GenesisSpec);
        }

        stateProvider.Commit(specProvider!.GenesisSpec);
        stateProvider.CommitTree(0);

        StateReader stateReader = new(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
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
    public async Task TearDownAsync() => await (_blockchainProcessor?.DisposeAsync() ?? default);

    [Test, MaxTime(Timeout.MaxTestTime)]
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
