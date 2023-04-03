// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
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
        _specProvider = RopstenSpecProvider.Instance;
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

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend();

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
            .WithNumber(RopstenSpecProvider.LondonBlockNumber)
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

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend();

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
            .WithNumber(RopstenSpecProvider.LondonBlockNumber)
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

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend();

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

        IList<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend();
        pickedTxs.Count.Should().Be(1);

        List<Transaction> expectedTxs = new() { transactions[0] };
        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }
}
