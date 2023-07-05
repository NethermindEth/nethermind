// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    public void should_pick_best_persistent_txs_to_broadcast(int threshold)
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

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(25)]
    [TestCase(50)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    public void should_skip_blob_txs_when_picking_best_persistent_txs_to_broadcast(int threshold)
    {
        _txPoolConfig = new TxPoolConfig() { PeerNotificationThreshold = threshold };
        _broadcaster = new TxBroadcaster(_comparer, TimerFactory.Default, _txPoolConfig, _headInfo, _logManager);
        _headInfo.CurrentBaseFee.Returns(0.GWei());

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

        (IList<Transaction> pickedTxs, IList<Transaction> pickedHashes) = _broadcaster.GetPersistentTxsToSend();

        int expectedCountTotal = Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount);
        int expectedCountOfBlobHashes = expectedCountTotal / 10 + 1;
        int expectedCountOfNonBlobTxs = expectedCountTotal - expectedCountOfBlobHashes;
        if (expectedCountOfNonBlobTxs > 0)
        {
            pickedTxs.Count.Should().Be(expectedCountOfNonBlobTxs);
        }
        else
        {
            pickedTxs.Should().BeNull();
        }

        if (expectedCountOfBlobHashes > 0)
        {
            pickedHashes.Count.Should().Be(expectedCountOfBlobHashes);
        }
        else
        {
            pickedHashes.Should().BeNull();
        }

        List<Transaction> expectedTxs = new();
        List<Transaction> expectedHashes = new();

        for (int i = 0; i < expectedCountTotal; i++)
        {
            Transaction tx = transactions[i];

            if (!tx.SupportsBlobs)
            {
                expectedTxs.Add(tx);
            }
            else
            {
                expectedHashes.Add(tx);
            }
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs);
        expectedHashes.Should().BeEquivalentTo(pickedHashes);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    public void should_not_pick_txs_with_GasPrice_lower_than_CurrentBaseFee(int threshold)
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

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    public void should_not_pick_1559_txs_with_MaxFeePerGas_lower_than_CurrentBaseFee(int threshold)
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
    [TestCase(128 * 1024, true)]
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
}
