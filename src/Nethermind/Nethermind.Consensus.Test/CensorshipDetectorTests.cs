// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class CensorshipDetectorTests
{
    private ILogManager _logManager;
    private WorldState _stateProvider;
    private IBlockTree _blockTree;
    private IBlockProcessor _blockProcessor;
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private IComparer<Transaction> _comparer;
    private TxPool.TxPool _txPool;
    private CensorshipDetector _censorshipDetector;

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;
        TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), _logManager);
        MemDb codeDb = new();
        _stateProvider = new WorldState(trieStore, codeDb, _logManager);
        _blockProcessor = Substitute.For<IBlockProcessor>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _txPool.DisposeAsync();
        _censorshipDetector.Dispose();
    }

    // Address Censorship is given to be false here since censorship is not being detected for any address.
    [Test]
    [Retry(3)]
    public void Censorship_when_address_censorship_is_false_and_high_paying_tx_censorship_is_true_for_all_blocks_in_main_cache()
    {
        _txPool = CreatePool();
        _censorshipDetector = new(_blockTree, _txPool, _comparer, _blockProcessor, _logManager, new CensorshipDetectorConfig() { });

        Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA, TestItem.AddressA);
        Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB, TestItem.AddressA);
        Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC, TestItem.AddressA);
        Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD, TestItem.AddressA);
        Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE, TestItem.AddressA);

        Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions([tx4]).WithParentHash(TestItem.KeccakA).TestObject;
        Hash256 blockHash1 = block1.Hash!;
        BlockProcessingWorkflow(block1);

        Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(0).WithTransactions([tx3]).WithParentHash(blockHash1).TestObject;
        Hash256 blockHash2 = block2.Hash!;
        BlockProcessingWorkflow(block2);

        Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(0).WithTransactions([tx2]).WithParentHash(blockHash2).TestObject;
        Hash256 blockHash3 = block3.Hash!;
        BlockProcessingWorkflow(block3);

        Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(0).WithTransactions([tx1]).WithParentHash(blockHash3).TestObject;
        BlockProcessingWorkflow(block4);

        Assert.That(() => _censorshipDetector.GetCensoredBlocks().Contains(new BlockNumberHash(block4)), Is.EqualTo(true).After(10, 1));
    }

    // Address Censorship is given to be false here since censorship is not being detected for any address.
    [Test]
    public void No_censorship_when_address_censorship_is_false_and_high_paying_tx_censorship_is_false_for_some_blocks_in_main_cache()
    {
        _txPool = CreatePool();
        _censorshipDetector = new(_blockTree, _txPool, _comparer, _blockProcessor, _logManager, new CensorshipDetectorConfig() { });

        Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA, TestItem.AddressA);
        Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB, TestItem.AddressA);
        Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC, TestItem.AddressA);
        Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD, TestItem.AddressA);
        Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE, TestItem.AddressA);

        // high-paying tx censorship: true
        Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions([tx4]).WithParentHash(TestItem.KeccakA).TestObject;
        Hash256 blockHash1 = block1.Hash!;
        BlockProcessingWorkflow(block1);

        // address censorship: false
        Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(0).WithTransactions([tx3, tx5]).WithParentHash(blockHash1).TestObject;
        Hash256 blockHash2 = block2.Hash!;
        BlockProcessingWorkflow(block2);

        // high-paying tx censorship: false
        Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(0).WithTransactions([tx2]).WithParentHash(blockHash2).TestObject;
        Hash256 blockHash3 = block3.Hash!;
        BlockProcessingWorkflow(block3);

        // high-paying tx censorship: false
        Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(0).WithTransactions([tx1]).WithParentHash(blockHash3).TestObject;
        BlockProcessingWorkflow(block4);

        Assert.That(() => _censorshipDetector.GetCensoredBlocks().Contains(new BlockNumberHash(block4)), Is.EqualTo(false).After(10, 1));
    }

    // High-Paying Tx Censorship is given to be false here.
    [Test]
    public void Censorship_when_high_paying_tx_censorship_is_false_and_address_censorship_is_true_for_all_blocks_in_main_cache()
    {
        _txPool = CreatePool();
        _censorshipDetector = new(
            _blockTree,
            _txPool,
            _comparer,
            _blockProcessor,
            _logManager,
            new CensorshipDetectorConfig()
            {
                AddressesForCensorshipDetection = [
                    TestItem.AddressA.ToString(),
                    TestItem.AddressB.ToString(),
                    TestItem.AddressC.ToString(),
                    TestItem.AddressD.ToString(),
                    TestItem.AddressE.ToString(),
                    TestItem.AddressF.ToString()]
            });

        Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA, TestItem.AddressA);
        Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB, TestItem.AddressB);
        Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC, TestItem.AddressC);
        Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD, TestItem.AddressD);
        Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE, TestItem.AddressE);
        Transaction tx6 = SubmitTxToPool(6, TestItem.PrivateKeyF, TestItem.AddressF);

        Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions([tx1, tx6]).WithParentHash(TestItem.KeccakA).TestObject;
        Hash256 blockHash1 = block1.Hash!;
        BlockProcessingWorkflow(block1);

        Transaction tx7 = SubmitTxToPool(7, TestItem.PrivateKeyA, TestItem.AddressA);
        Transaction tx8 = SubmitTxToPool(8, TestItem.PrivateKeyF, TestItem.AddressF);

        Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(0).WithTransactions([tx2, tx8]).WithParentHash(blockHash1).TestObject;
        Hash256 blockHash2 = block2.Hash!;
        BlockProcessingWorkflow(block2);

        Transaction tx9 = SubmitTxToPool(9, TestItem.PrivateKeyB, TestItem.AddressB);
        Transaction tx10 = SubmitTxToPool(10, TestItem.PrivateKeyF, TestItem.AddressF);

        Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(0).WithTransactions([tx3, tx10]).WithParentHash(blockHash2).TestObject;
        Hash256 blockHash3 = block3.Hash!;
        BlockProcessingWorkflow(block3);

        Transaction tx11 = SubmitTxToPool(11, TestItem.PrivateKeyC, TestItem.AddressC);
        Transaction tx12 = SubmitTxToPool(12, TestItem.PrivateKeyF, TestItem.AddressF);

        Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(0).WithTransactions([tx4, tx12]).WithParentHash(blockHash3).TestObject;
        BlockProcessingWorkflow(block4);

        Assert.That(() => _censorshipDetector.GetCensoredBlocks().Contains(new BlockNumberHash(block4)), Is.EqualTo(true).After(10, 1));
    }

    // High-Paying Tx Censorship is given to be false here.
    [Test]
    public void No_censorship_when_high_paying_tx_censorship_is_false_and_address_censorship_is_false_for_some_blocks_in_main_cache()
    {
        _txPool = CreatePool();
        _censorshipDetector = new(
            _blockTree,
            _txPool,
            _comparer,
            _blockProcessor,
            _logManager,
            new CensorshipDetectorConfig()
            {
                AddressesForCensorshipDetection = [
                    TestItem.AddressA.ToString(),
                    TestItem.AddressB.ToString(),
                    TestItem.AddressC.ToString(),
                    TestItem.AddressD.ToString(),
                    TestItem.AddressE.ToString()]
            });

        Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA, TestItem.AddressA);
        Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB, TestItem.AddressB);
        Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC, TestItem.AddressC);
        Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD, TestItem.AddressD);
        Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE, TestItem.AddressE);

        // address censorship: false
        Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions([tx3, tx4, tx5]).WithParentHash(TestItem.KeccakA).TestObject;
        Hash256 blockHash1 = block1.Hash!;
        BlockProcessingWorkflow(block1);

        Transaction tx6 = SubmitTxToPool(6, TestItem.PrivateKeyC, TestItem.AddressC);
        Transaction tx7 = SubmitTxToPool(7, TestItem.PrivateKeyD, TestItem.AddressD);
        Transaction tx8 = SubmitTxToPool(8, TestItem.PrivateKeyE, TestItem.AddressE);

        // address censorship: false
        Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(0).WithTransactions([tx7, tx8]).WithParentHash(blockHash1).TestObject;
        Hash256 blockHash2 = block2.Hash!;
        BlockProcessingWorkflow(block2);

        Transaction tx9 = SubmitTxToPool(9, TestItem.PrivateKeyD, TestItem.AddressD);
        Transaction tx10 = SubmitTxToPool(10, TestItem.PrivateKeyE, TestItem.AddressE);

        // address censorship: true
        Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(0).WithTransactions([tx1, tx10]).WithParentHash(blockHash2).TestObject;
        Hash256 blockHash3 = block3.Hash!;
        BlockProcessingWorkflow(block3);

        // address censorship: false
        Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(0).WithTransactions([tx2, tx6, tx9]).WithParentHash(blockHash3).TestObject;
        BlockProcessingWorkflow(block4);

        Assert.That(() => _censorshipDetector.GetCensoredBlocks().Contains(new BlockNumberHash(block4)), Is.EqualTo(false).After(10, 1));
    }

    private TxPool.TxPool CreatePool(bool eip1559Enabled = true)
    {
        if (eip1559Enabled)
        {
            _specProvider = Substitute.For<ISpecProvider>();
            _specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        }
        else
        {
            _specProvider = MainnetSpecProvider.Instance;
        }

        _blockTree = Substitute.For<IBlockTree>();
        _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(1_000_000).TestObject);
        _blockTree.Head.Returns(Build.A.Block.WithNumber(1_000_000).TestObject);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
        _comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();

        return new(
            _ethereumEcdsa,
            new BlobTxStorage(),
            new ChainHeadInfoProvider(_specProvider, _blockTree, _stateProvider, new CodeInfoRepository()),
            new TxPoolConfig(),
            new TxValidator(_specProvider.ChainId),
            _logManager,
            _comparer);
    }

    private void BlockProcessingWorkflow(Block block)
    {
        _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
        Assert.That(() => _censorshipDetector.BlockPotentiallyCensored(block.Number, block.Hash), Is.EqualTo(true).After(10, 1));

        foreach (Transaction tx in block.Transactions)
        {
            _txPool.RemoveTransaction(tx.Hash);
        }
    }

    private Transaction SubmitTxToPool(int maxPriorityFeePerGas, PrivateKey privateKey, Address address)
    {
        Transaction tx = Build.A.Transaction.
                        WithType(TxType.EIP1559).
                        WithMaxFeePerGas(20.Wei()).
                        WithMaxPriorityFeePerGas(maxPriorityFeePerGas.Wei()).
                        WithTo(address).
                        SignedAndResolved(_ethereumEcdsa, privateKey).
                        TestObject;
        _stateProvider.CreateAccount(tx.SenderAddress, 1_000_000.Wei());
        AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
        result.Should().Be(AcceptTxResult.Accepted);
        return tx;
    }
}
