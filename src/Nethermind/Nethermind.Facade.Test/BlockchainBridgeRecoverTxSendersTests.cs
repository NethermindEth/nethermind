// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Find;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test;

[TestFixture(true)]
[TestFixture(false)]
public class BlockchainBridgeRecoverTxSendersTests(bool compactReceipts)
{
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private CountingReceiptFinder _receiptFinder = null!;
    private IContainer _container = null!;
    private IBlockchainBridge _blockchainBridge = null!;
    private IEthereumEcdsa _ecdsa = null!;

    [SetUp]
    public void SetUp()
    {
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        _receiptFinder = new CountingReceiptFinder();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(Substitute.For<IBlockTree>())
            .AddSingleton<IReceiptFinder>(_receiptFinder)
            .AddSingleton(Substitute.For<ILogFinder>())
            .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
            .AddSingleton(Substitute.For<IStateReader>())
            .Build();

        _ecdsa = _container.Resolve<IEthereumEcdsa>();
        _receiptFinder.Inner = CreateStorage();
        _blockchainBridge = _container.Resolve<IBlockchainBridge>();
    }

    [TearDown]
    public void TearDown()
    {
        _container.Dispose();
        _receiptsDb.Dispose();
    }

    [Test]
    public void Takes_stored_senders_without_decoding_the_receipts([Values] bool cached)
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);
        StoreReceipts(block, TestItem.AddressC, TestItem.AddressD);
        if (cached) MoveReceiptsIntoCache(block);

        _blockchainBridge.RecoverTxSenders(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressC, TestItem.AddressD }));
            Assert.That(_receiptFinder.FullReads, Is.Zero);
        }
    }

    [Test]
    public void Recovers_a_sender_the_receipt_does_not_store([Values] bool cached)
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);
        StoreReceipts(block, TestItem.AddressC, null);
        if (cached) MoveReceiptsIntoCache(block);

        _blockchainBridge.RecoverTxSenders(block);

        Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressC, TestItem.AddressB }));
    }

    [Test]
    public void Falls_back_to_the_full_read_when_the_finder_has_no_iterator_for_the_block()
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);
        StoreReceipts(block, TestItem.AddressC, TestItem.AddressD);
        ((PersistentReceiptStorage)_receiptFinder.Inner).MigratedBlockNumber = block.Number + 1;

        _blockchainBridge.RecoverTxSenders(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressC, TestItem.AddressD }));
            Assert.That(_receiptFinder.FullReads, Is.EqualTo(1));
        }
    }

    [Test]
    public void Recovers_every_sender_when_the_receipt_count_does_not_match()
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);
        StoreReceipts(block, TestItem.AddressC);

        _blockchainBridge.RecoverTxSenders(block);

        Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
    }

    [Test]
    public void Recovers_every_sender_when_no_receipts_are_stored()
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);

        _blockchainBridge.RecoverTxSenders(block);

        Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
    }

    [Test]
    public void Keeps_known_senders_and_reads_no_receipts()
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB);
        block.Transactions[0].SenderAddress = TestItem.AddressE;
        block.Transactions[1].SenderAddress = TestItem.AddressF;
        StoreReceipts(block, TestItem.AddressC, TestItem.AddressD);

        _blockchainBridge.RecoverTxSenders(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressE, TestItem.AddressF }));
            Assert.That(_receiptFinder.IteratorReads + _receiptFinder.FullReads, Is.Zero);
        }
    }

    [Test]
    public void Sets_the_senders_a_full_receipt_read_recovers()
    {
        Block block = BuildBlock(TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC);
        StoreReceipts(block, TestItem.AddressD, null, TestItem.AddressE);
        Block copy = Rlp.Decode<Block>(Rlp.Encode(block).Bytes);

        _blockchainBridge.RecoverTxSenders(block);
        CreateStorage().Get(copy);

        Assert.That(block.Transactions.Select(static tx => tx.SenderAddress), Is.EqualTo(copy.Transactions.Select(static tx => tx.SenderAddress)));
    }

    /// <summary>
    /// Caches the stored receipts as a full read does without recovering senders, then drops them from the db, so only
    /// the cached receipts can answer.
    /// </summary>
    private void MoveReceiptsIntoCache(Block block)
    {
        _receiptFinder.Inner.Get(Rlp.Decode<Block>(Rlp.Encode(block).Bytes), recover: true, recoverSender: false);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Remove(block.Hash!.Bytes);
    }

    private PersistentReceiptStorage CreateStorage()
    {
        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
        return new PersistentReceiptStorage(
            _receiptsDb,
            specProvider,
            new ReceiptsRecovery(_ecdsa, specProvider),
            Substitute.For<IBlockTree>(),
            Substitute.For<IBlockStore>(),
            new ReceiptConfig(),
            new ReceiptArrayStorageDecoder(compactReceipts))
        { MigratedBlockNumber = 0 };
    }

    private Block BuildBlock(params PrivateKey[] signers)
    {
        Transaction[] transactions = signers
            .Select((key, i) => Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(_ecdsa, key).TestObject)
            .ToArray();
        foreach (Transaction transaction in transactions)
        {
            transaction.SenderAddress = null;
        }

        return Build.A.Block.WithNumber(1).WithTransactions(transactions).WithReceiptsRoot(TestItem.KeccakA).TestObject;
    }

    private void StoreReceipts(Block block, params Address?[] senders)
    {
        TxReceipt[] receipts = senders.Select(static sender => Build.A.Receipt.WithSender(sender!).TestObject).ToArray();
        byte[] encoded = new ReceiptArrayStorageDecoder(compactReceipts).EncodeAsBytes(receipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = encoded;
    }

    private sealed class CountingReceiptFinder : IReceiptFinder
    {
        public IReceiptFinder Inner { get; set; } = null!;
        public int FullReads { get; private set; }
        public int IteratorReads { get; private set; }

        public Hash256? FindBlockHash(Hash256 txHash) => Inner.FindBlockHash(txHash);

        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
        {
            FullReads++;
            return Inner.Get(block, recover, recoverSender);
        }

        public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
        {
            FullReads++;
            return Inner.Get(blockHash, recover);
        }

        public bool CanGetReceiptsByHash(ulong blockNumber) => Inner.CanGetReceiptsByHash(blockNumber);

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            IteratorReads++;
            return Inner.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
        }
    }
}
