// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.TxPool.Test;

[TestFixture]
public class TxBroadcasterTests
{
    private ILogManager _logManager;
    private ISpecProvider _specProvider;
    private IBlockTree _blockTree;
    private IComparer<Transaction> _comparer;
    private TxBroadcaster _broadcaster;
    private EthereumEcdsa _ethereumEcdsa;
    private TxPoolConfig _txPoolConfig;
    private IChainHeadInfoProvider _headInfo;

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;
        _specProvider = MainnetSpecProvider.Instance;
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, _logManager);
        _blockTree = Substitute.For<IBlockTree>();
        _comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();
        _txPoolConfig = new TxPoolConfig();
        _headInfo = Substitute.For<IChainHeadInfoProvider>();
    }

    [TearDown]
    public void TearDown() => _broadcaster?.Dispose();

    [Test]
    public async Task should_not_broadcast_persisted_tx_to_peer_too_quickly()
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = 100 };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithGasPrice(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
        peer.Id.Returns(TestItem.PublicKeyA);

        _broadcaster.AddPeer(peer);

        peer.Received(0).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), true);

        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();

        peer.Received(1).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), true);

        await Task.Delay(TimeSpan.FromMilliseconds(1001));

        peer.Received(1).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), true);

        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();
        _broadcaster.BroadcastPersistentTxs();

        peer.Received(2).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), true);
    }

    [Test]
    public void should_pick_best_persistent_txs_to_broadcast([Values(1, 2, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithGasPrice(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().TransactionsToSend;

        int expectedCount = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount);
        pickedTxs.Count.Should().Be(expectedCount);

        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }

    [Test]
    public void should_add_light_form_of_blob_txs_to_persistent_txs_but_not_return_if_requested([Values(true, false)] bool isBlob)
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        Transaction tx = Build.A.Transaction
            .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
            .WithShardBlobTxTypeAndFieldsIfBlobTx()
            .SignedAndResolved().TestObject;

        _broadcaster.Broadcast(tx, true);
        _broadcaster.GetSnapshot().Length.Should().Be(1);
        _broadcaster.GetSnapshot().FirstOrDefault().Should().BeEquivalentTo(isBlob ? new LightTransaction(tx) : tx);

        _broadcaster.TryGetPersistentTx(tx.Hash, out Transaction returnedTx).Should().Be(!isBlob);
        returnedTx.Should().BeEquivalentTo(isBlob ? null : tx);
    }

    [Test]
    public void should_announce_details_of_full_blob_tx()
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .SignedAndResolved().TestObject;

        Transaction lightTx = new LightTransaction(tx);

        int size = tx.GetLength();
        size.Should().Be(131320);
        lightTx.GetLength().Should().Be(size);

        _broadcaster.Broadcast(tx, true);
        _broadcaster.GetSnapshot().Length.Should().Be(1);
        _broadcaster.GetSnapshot().FirstOrDefault().Should().BeEquivalentTo(lightTx);

        ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
        peer.Id.Returns(TestItem.PublicKeyA);

        _broadcaster.AddPeer(peer);
        _broadcaster.BroadcastPersistentTxs();

        peer.Received(1).SendNewTransactions(Arg.Is<IEnumerable<Transaction>>(t => t.FirstOrDefault().GetLength() == size), false);
    }

    [Test]
    public void should_skip_large_txs_when_picking_best_persistent_txs_to_broadcast([Values(1, 2, 25, 50, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        // add 256 transactions, 10% of them is large
        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            bool isLarge = i % 10 == 0;
            transactions[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithGasPrice((addedTxsCount - i - 1).GWei())
                .WithData(isLarge ? new byte[4 * 1024] : Array.Empty<byte>()) //some part of txs (10%) is large (>4KB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        // count numbers of expected hashes and full transactions
        int expectedCountTotal = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount);
        int expectedCountOfHashes = expectedCountTotal / 10 + 1;
        int expectedCountOfFullTxs = expectedCountTotal - expectedCountOfHashes;

        // prepare list of expected full transactions and hashes
        (IList<Transaction> expectedFullTxs, IList<Hash256> expectedHashes) = GetTxsAndHashesExpectedToBroadcast(transactions, expectedCountTotal);

        // get hashes and full transactions to broadcast
        (IList<Transaction> pickedFullTxs, IList<Transaction> pickedHashes) = _broadcaster.GetPersistentTxsToSend();

        // check if numbers of full transactions and hashes are correct
        CheckCorrectness(pickedFullTxs, expectedCountOfFullTxs);
        CheckCorrectness(pickedHashes, expectedCountOfHashes);

        // check if full transactions and hashes returned by broadcaster are as expected
        expectedFullTxs.Should().BeEquivalentTo(pickedFullTxs);
        expectedHashes.Should().BeEquivalentTo(pickedHashes.Select(t => t.Hash).ToArray());
    }

    [Test]
    public void should_skip_blob_txs_when_picking_best_persistent_txs_to_broadcast([Values(1, 2, 25, 50, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        // add 256 transactions, 10% of them is blob type
        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            bool isBlob = i % 10 == 0;
            transactions[i] = Build.A.Transaction
                .WithGasPrice((addedTxsCount - i - 1).GWei())
                .WithType(isBlob ? TxType.Blob : TxType.Legacy) //some part of txs (10%) is blob type
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        // count numbers of expected hashes and full transactions
        int expectedCountTotal = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount);
        int expectedCountOfBlobHashes = expectedCountTotal / 10 + 1;
        int expectedCountOfNonBlobTxs = expectedCountTotal - expectedCountOfBlobHashes;

        // prepare list of expected full transactions and hashes
        (IList<Transaction> expectedFullTxs, IList<Hash256> expectedHashes) = GetTxsAndHashesExpectedToBroadcast(transactions, expectedCountTotal);

        // get hashes and full transactions to broadcast
        (IList<Transaction> pickedFullTxs, IList<Transaction> pickedHashes) = _broadcaster.GetPersistentTxsToSend();

        // check if numbers of full transactions and hashes are correct
        CheckCorrectness(pickedFullTxs, expectedCountOfNonBlobTxs);
        CheckCorrectness(pickedHashes, expectedCountOfBlobHashes);

        // check if full transactions and hashes returned by broadcaster are as expected
        expectedFullTxs.Should().BeEquivalentTo(pickedFullTxs);
        expectedHashes.Should().BeEquivalentTo(pickedHashes.Select(t => t.Hash).ToArray());
    }

    [Test]
    public void should_not_pick_txs_with_GasPrice_lower_than_CurrentBaseFee([Values(1, 2, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        const int currentBaseFeeInGwei = 250;
        _headInfo.CurrentBaseFee.Returns(currentBaseFeeInGwei.GWei());
        Block headBlock = Build.A.Block
            .WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBaseFeePerGas(currentBaseFeeInGwei.GWei())
            .TestObject;
        _blockTree.Head.Returns(headBlock);

        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithGasPrice(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().TransactionsToSend;

        int expectedCount = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount - currentBaseFeeInGwei);
        pickedTxs.Count.Should().Be(expectedCount);

        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }

    [TestCase(0, false)]
    [TestCase(69, false)]
    [TestCase(70, true)]
    [TestCase(100, true)]
    [TestCase(150, true)]
    public void should_not_broadcast_tx_with_MaxFeePerGas_lower_than_70_percent_of_CurrentBaseFee(int maxFeePerGas, bool shouldBroadcast)
    {
        _headInfo.CurrentBaseFee.Returns((UInt256)100);

        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
        peer.Id.Returns(TestItem.PublicKeyA);
        _broadcaster.AddPeer(peer);

        Transaction transaction = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas((UInt256)maxFeePerGas)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        _broadcaster.Broadcast(transaction, true);

        // tx should be immediately broadcasted only if MaxFeePerGas is equal at least 70% of current base fee
        peer.Received(shouldBroadcast ? 1 : 0).SendNewTransaction(Arg.Any<Transaction>());

        // tx should always be added to persistent collection, without any fee restrictions
        _broadcaster.GetSnapshot().Length.Should().Be(1);
    }

    [Test]
    public void should_not_pick_1559_txs_with_MaxFeePerGas_lower_than_CurrentBaseFee([Values(1, 2, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        const int currentBaseFeeInGwei = 250;
        _headInfo.CurrentBaseFee.Returns(currentBaseFeeInGwei.GWei());
        Block headBlock = Build.A.Block
            .WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBaseFeePerGas(currentBaseFeeInGwei.GWei())
            .TestObject;
        _blockTree.Head.Returns(headBlock);

        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().TransactionsToSend;

        int expectedCount = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount - currentBaseFeeInGwei);
        pickedTxs.Count.Should().Be(expectedCount);

        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs, o => o.Excluding(transaction => transaction.MaxFeePerGas));
    }

    [Test]
    public void should_not_pick_blob_txs_with_MaxFeePerBlobGas_lower_than_CurrentPricePerBlobGas([Values(1, 2, 99, 100, 101, 1000)] int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        const int currentPricePerBlobGasInGwei = 250;
        _headInfo.CurrentPricePerBlobGas.Returns(currentPricePerBlobGasInGwei.GWei());

        // add 256 transactions with MaxFeePerBlobGas 0-255
        int addedTxsCount = TestItem.PrivateKeys.Length;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerBlobGas(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i])
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }

        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        // count number of expected hashes to broadcast
        int expectedCount = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount - currentPricePerBlobGasInGwei);

        // prepare list of expected hashes to broadcast
        List<Transaction> expectedTxs = new();
        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        // get actual hashes to broadcast
        IList<Transaction> pickedHashes = _broadcaster.GetPersistentTxsToSend().HashesToSend;

        // check if number of hashes to broadcast is correct
        pickedHashes.Count.Should().Be(expectedCount);

        // check if number of hashes to broadcast (with MaxFeePerBlobGas >= current) is correct
        expectedTxs.Count(t => t.MaxFeePerBlobGas >= (UInt256)currentPricePerBlobGasInGwei).Should().Be(expectedCount);
    }

    [Test]
    public void should_pick_tx_with_lowest_nonce_from_bucket()
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = 5 };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        const int addedTxsCount = 5;
        Transaction[] transactions = new Transaction[addedTxsCount];

        for (int i = 0; i < addedTxsCount; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((UInt256)i)
                .WithGasPrice(i.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;

            _broadcaster.Broadcast(transactions[i], true);
        }
        _broadcaster.GetSnapshot().Length.Should().Be(addedTxsCount);

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().TransactionsToSend;
        pickedTxs.Count.Should().Be(1);

        List<Transaction> expectedTxs = new() { transactions[0] };
        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }

    [Test]
    public void should_broadcast_local_tx_immediately_after_receiving_it()
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
        peer.Id.Returns(TestItem.PublicKeyA);
        _broadcaster.AddPeer(peer);

        Transaction localTx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        _broadcaster.Broadcast(localTx, true);

        peer.Received().SendNewTransaction(localTx);
    }

    [Test]
    public void should_broadcast_full_local_tx_immediately_after_receiving_it()
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        ITxPoolPeer eth68Handler = new Eth68ProtocolHandler(session,
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IPooledTxsRequestor>(),
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, Keccak.Zero),
            Substitute.For<ILogManager>());
        _broadcaster.AddPeer(eth68Handler);

        Transaction localTx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        _broadcaster.Broadcast(localTx, true);

        session.Received(1).DeliverMessage(Arg.Any<TransactionsMessage>());
    }

    [Test]
    public void should_broadcast_hash_of_blob_local_tx_to_eth68_peers_immediately_after_receiving_it()
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        ISession session67 = Substitute.For<ISession>();
        session67.Node.Returns(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        ITxPoolPeer eth67Handler = new Eth67ProtocolHandler(session67,
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IPooledTxsRequestor>(),
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, Keccak.Zero),
            Substitute.For<ILogManager>());

        ISession session68 = Substitute.For<ISession>();
        session68.Node.Returns(new Node(TestItem.PublicKeyB, TestItem.IPEndPointB));
        ITxPoolPeer eth68Handler = new Eth68ProtocolHandler(session68,
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IPooledTxsRequestor>(),
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, Keccak.Zero),
            Substitute.For<ILogManager>());

        Transaction localTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        _broadcaster.AddPeer(eth67Handler);
        _broadcaster.AddPeer(eth68Handler);

        _broadcaster.Broadcast(localTx, true);

        session67.DidNotReceive().DeliverMessage(Arg.Any<TransactionsMessage>());
        session67.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage>());
        session67.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());

        session68.DidNotReceive().DeliverMessage(Arg.Any<TransactionsMessage>());
        session68.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage>());
        session68.Received(1).DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());
    }

    [TestCase(1_000, true)]
    [TestCase(4 * 1024, true)]
    [TestCase(4 * 1024 + 1, false)]
    [TestCase(128 * 1024, false)]
    [TestCase(128 * 1024 + 1, false)]
    [TestCase(1_000_000, false)]
    public void should_broadcast_full_local_tx_up_to_max_size_and_only_announce_if_larger(int txSize, bool shouldBroadcastFullTx)
    {
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        ISession session68 = Substitute.For<ISession>();
        session68.Node.Returns(new Node(TestItem.PublicKeyB, TestItem.IPEndPointB));
        ITxPoolPeer eth68Handler = new Eth68ProtocolHandler(session68,
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IPooledTxsRequestor>(),
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, Keccak.Zero),
            Substitute.For<ILogManager>());

        Transaction localTx = Build.A.Transaction
            .WithData(new byte[txSize])
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        int draftTxSize = localTx.GetLength();
        if (draftTxSize > txSize)
        {
            localTx = Build.A.Transaction
                .WithData(new byte[2 * txSize - draftTxSize])
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
        }
        localTx.GetLength().Should().Be(txSize);

        _broadcaster.AddPeer(eth68Handler);
        _broadcaster.Broadcast(localTx, true);

        if (shouldBroadcastFullTx)
        {
            session68.Received(1).DeliverMessage(Arg.Any<TransactionsMessage>());
            session68.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());
        }
        else
        {
            session68.DidNotReceive().DeliverMessage(Arg.Any<TransactionsMessage>());
            session68.Received(1).DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());
        }
        session68.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage>());
    }

    [TestCase(true, true, 1)]
    [TestCase(false, true, 0)]
    [TestCase(true, false, 0)]
    [TestCase(false, false, 0)]
    public void should_check_tx_policy_for_broadcast(bool canGossipTransactions, bool shouldGossipTransaction, int received)
    {
        ITxGossipPolicy txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager, txGossipPolicy);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        ITxPoolPeer eth68Handler = new Eth68ProtocolHandler(session,
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IPooledTxsRequestor>(),
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, Keccak.Zero),
            Substitute.For<ILogManager>());
        _broadcaster.AddPeer(eth68Handler);

        Transaction localTx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        txGossipPolicy.CanGossipTransactions.Returns(canGossipTransactions);
        txGossipPolicy.ShouldGossipTransaction(localTx).Returns(shouldGossipTransaction);

        _broadcaster.Broadcast(localTx, true);

        session.Received(received).DeliverMessage(Arg.Any<TransactionsMessage>());
    }

    [Test]
    public void should_rebroadcast_all_persistent_transactions_if_PeerNotificationThreshold_is_100([Values(true, false)] bool shouldBroadcastAll)
    {
        _txPoolConfig = new TxPoolConfig() { Size = 100, PeerNotificationThreshold = shouldBroadcastAll ? 100 : 5 };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        for (int i = 0; i < _txPoolConfig.Size; i++)
        {
            Transaction tx = Build.A.Transaction
                .WithNonce((UInt256)i)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _broadcaster.Broadcast(tx, true);
        }

        Transaction[] pickedTxs = _broadcaster.GetPersistentTxsToSend().TransactionsToSend.ToArray();
        pickedTxs.Length.Should().Be(shouldBroadcastAll ? 100 : 1);

        for (int i = 0; i < pickedTxs.Length; i++)
        {
            pickedTxs[i].Nonce.Should().Be((UInt256)i);
        }
    }

    [TestCase(0, 0)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(7, 4)]
    [TestCase(8, 5)]
    [TestCase(100, 70)]
    [TestCase(9999, 6999)]
    [TestCase(10000, 7000)]
    public void should_calculate_baseFeeThreshold_correctly(int baseFee, int expectedThreshold)
    {
        _headInfo.CurrentBaseFee.Returns((UInt256)baseFee);
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _broadcaster.CalculateBaseFeeThreshold().Should().Be((UInt256)expectedThreshold);
    }

    [Test]
    public void calculation_of_baseFeeThreshold_should_handle_overflow_correctly([Values(0, 70, 100, 101, 500)] int threshold, [Values(2, 3, 4, 5, 6, 7, 8, 9, 10, 11)] int divisor)
    {
        UInt256.Divide(UInt256.MaxValue, (UInt256)divisor, out UInt256 baseFee);
        _headInfo.CurrentBaseFee.Returns(baseFee);

        _txPoolConfig = new TxPoolConfig() { MinBaseFeeThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);

        UInt256.Divide(baseFee, 100, out UInt256 onePercentOfBaseFee);
        bool overflow = UInt256.MultiplyOverflow(onePercentOfBaseFee, (UInt256)threshold, out UInt256 lessAccurateBaseFeeThreshold);

        _broadcaster.CalculateBaseFeeThreshold().Should().Be(
            UInt256.MultiplyOverflow(baseFee, (UInt256)threshold, out UInt256 baseFeeThreshold)
                ? overflow ? UInt256.MaxValue : lessAccurateBaseFeeThreshold
                : baseFeeThreshold);
    }

    private (IList<Transaction> expectedTxs, IList<Hash256> expectedHashes) GetTxsAndHashesExpectedToBroadcast(Transaction[] transactions, int expectedCountTotal)
    {
        List<Transaction> expectedTxs = new();
        List<Hash256> expectedHashes = new();

        for (int i = 0; i < expectedCountTotal; i++)
        {
            Transaction tx = transactions[i];

            if (tx.CanBeBroadcast())
            {
                expectedTxs.Add(tx);
            }
            else
            {
                expectedHashes.Add(tx.Hash);
            }
        }

        return (expectedTxs, expectedHashes);
    }

    private void CheckCorrectness(IList<Transaction> pickedTxs, int expectedCount)
    {
        if (expectedCount > 0)
        {
            pickedTxs.Count.Should().Be(expectedCount);
        }
        else
        {
            pickedTxs.Should().BeNull();
        }
    }
}
