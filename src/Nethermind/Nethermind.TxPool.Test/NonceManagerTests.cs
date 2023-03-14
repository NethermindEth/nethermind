// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class NonceManagerTests
{
    private ISpecProvider _specProvider;
    private IStateProvider _stateProvider;
    private IBlockTree _blockTree;
    private ChainHeadInfoProvider _headInfo;
    private INonceManager _nonceManager;

    [SetUp]
    public void Setup()
    {
        ILogManager logManager = LimboLogs.Instance;
        _specProvider = MainnetSpecProvider.Instance;
        var trieStore = new TrieStore(new MemDb(), logManager);
        var codeDb = new MemDb();
        _stateProvider = new StateProvider(trieStore, codeDb, logManager);
        _blockTree = Substitute.For<IBlockTree>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        _blockTree.Head.Returns(block);
        _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);

        _headInfo = new ChainHeadInfoProvider(_specProvider, _blockTree, _stateProvider);
        _nonceManager = new NonceManager(_headInfo.AccountStateProvider);
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(0);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(1);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(1);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out UInt256 nonce))
        {
            nonce.Should().Be(0);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out UInt256 nonce))
        {
            nonce.Should().Be(1);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out UInt256 nonce))
        {
            nonce.Should().Be(2);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out UInt256 nonce))
        {
            nonce.Should().Be(2);
            locker.Accept();
        }
    }

    [Test]
    [Repeat(10)]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
    {
        const int reservationsCount = 1000;

        ConcurrentQueue<UInt256> nonces = new();

        var result = Parallel.For(0, reservationsCount, i =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce);
            locker.Accept();
            nonces.Enqueue(nonce);
        });

        result.IsCompleted.Should().BeTrue();
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce);
        nonces.Enqueue(nonce);
        nonce.Should().Be(new UInt256(reservationsCount));
        nonces.OrderBy(n => n).Should().BeEquivalentTo(Enumerable.Range(0, reservationsCount + 1).Select(i => new UInt256((uint)i)));
    }

    [Test]
    public void should_pick_account_nonce_as_initial_value()
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        Account account = new(0);
        accountStateProvider.GetAccount(TestItem.AddressA).Returns(account);
        _nonceManager = new NonceManager(accountStateProvider);
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(0);
        }

        accountStateProvider.GetAccount(TestItem.AddressA).Returns(account.WithChangedNonce(10));
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(10);
        }
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_if_TxWithNonceReceived()
    {
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 4))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(0);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(1);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 2))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(3);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(5);
            locker.Accept();
        }
    }

    [Test]
    public void should_reuse_nonce_if_tx_rejected()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(0);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(0);
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 1)) { }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce))
        {
            nonce.Should().Be(1);
            locker.Accept();
        }
    }

    [Test]
    [Repeat(10)]
    public void should_lock_on_same_account()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce);
        nonce.Should().Be(0);
        Task task = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 _);
        });
        TimeSpan ts = TimeSpan.FromMilliseconds(1000);
        task.Wait(ts);
        task.IsCompleted.Should().Be(false);
    }

    [Test]
    [Repeat(10)]
    public void should_not_lock_on_different_accounts()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out UInt256 nonce);
        nonce.Should().Be(0);
        Task task = Task.Run(() =>
        {
            using NonceLocker locker2 = _nonceManager.ReserveNonce(TestItem.AddressB, out UInt256 nonce2);
            nonce2.Should().Be(0);
        });
        TimeSpan ts = TimeSpan.FromMilliseconds(1000);
        task.Wait(ts);
        task.IsCompleted.Should().Be(true);
    }
}
