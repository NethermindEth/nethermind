//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
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
    
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    [TestCase(-10)]
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

        List<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().ToList();

        int expectedCount = threshold <= 0 ? 0 : Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount);
        pickedTxs.Count.Should().Be(expectedCount);
        
        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }
    
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(1000)]
    [TestCase(-10)]
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

        List<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().ToList();

        int expectedCount = threshold <= 0 ? 0 : Math.Min(addedTxsCount * threshold / 100 + 1, addedTxsCount - currentBaseFeeInGwei);
        pickedTxs.Count.Should().Be(expectedCount);

        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs);
    }
    
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1000)]
    [TestCase(-10)]
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

        List<Transaction> pickedTxs = _broadcaster.GetPersistentTxsToSend().ToList();

        int expectedCount = threshold <= 0 ? 0 : addedTxsCount - currentBaseFeeInGwei;
        pickedTxs.Count.Should().Be(expectedCount);

        List<Transaction> expectedTxs = new();

        for (int i = 1; i <= expectedCount; i++)
        {
            expectedTxs.Add(transactions[addedTxsCount - i]);
        }

        expectedTxs.Should().BeEquivalentTo(pickedTxs, o => o.Excluding(transaction => transaction.MaxFeePerGas));
    }
}
