// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NonceManagerTests
{
    private ISpecProvider _specProvider;
    private TestReadOnlyStateProvider _stateProvider;
    private IBlockTree _blockTree;
    private ChainHeadInfoProvider _headInfo;
    private INonceManager _nonceManager;
    private ITxPool _txPool;

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        _stateProvider = new TestReadOnlyStateProvider();
        _blockTree = Substitute.For<IBlockTree>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        _blockTree.Head.Returns(block);
        _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);

        _headInfo = new ChainHeadInfoProvider(
            new ChainHeadSpecProvider(_specProvider, _blockTree),
            _blockTree,
            _stateProvider);
        _nonceManager = new NonceManager(_headInfo.ReadOnlyStateProvider);
        _txPool = Substitute.For<ITxPool>();
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
            locker.Accept();
        }
    }

    [Test]
    [Explicit]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
    {
        const int reservationsCount = 1000;

        ConcurrentQueue<ulong> nonces = new();

        ParallelLoopResult result = Parallel.For(0, reservationsCount, i =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce);
            locker.Accept();
            nonces.Enqueue(nonce);
        });

        Assert.That(result.IsCompleted, Is.True);
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce);
        nonces.Enqueue(nonce);
        Assert.That(nonce, Is.EqualTo((ulong)reservationsCount));
        Assert.That(nonces.OrderBy(n => n), Is.EqualTo(Enumerable.Range(0, reservationsCount + 1).Select(i => (ulong)i)));
    }

    [Test]
    public void should_pick_account_nonce_as_initial_value()
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        _nonceManager = new NonceManager(accountStateProvider);

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(10UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_if_TxWithNonceReceived()
    {
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 4))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 2))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(3UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(5UL));
            locker.Accept();
        }
    }

    [TestCase(new ulong[] { 5 }, new ulong[] { 5 }, false, 6UL, TestName = "ReserveNonce_should_skip_used_nonce_when_account_nonce_catches_up")]
    [TestCase(new ulong[] { 5, 6 }, new ulong[] { 5, 6 }, false, 7UL, TestName = "ReserveNonce_should_skip_run_of_used_nonces_when_account_nonce_catches_up")]
    [TestCase(new ulong[] { 5 }, new ulong[] { 5 }, true, 6UL, TestName = "ReserveNonce_should_skip_used_nonce_pending_in_blob_pool")]
    [TestCase(new ulong[] { 5 }, new ulong[0], false, 5UL, TestName = "ReserveNonce_should_reuse_used_nonce_that_left_the_pool")]
    [TestCase(new ulong[] { 5, 6 }, new ulong[] { 5 }, false, 6UL, TestName = "ReserveNonce_should_stop_at_first_used_nonce_that_left_the_pool")]
    public void ReserveNonce_should_skip_used_nonces_still_pending_when_account_nonce_catches_up(ulong[] usedNonces, ulong[] pendingNonces, bool blobPool, ulong expectedNonce)
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        _nonceManager = new NonceManager(accountStateProvider);

        foreach (ulong usedNonce in usedNonces)
        {
            using NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, usedNonce);
            locker.Accept();
        }

        Transaction[] pending = pendingNonces.Select(static n => Build.A.Transaction.WithNonce(n).TestObject).ToArray();
        if (blobPool)
        {
            _txPool.GetPendingLightBlobTransactionsBySender(TestItem.AddressA).Returns(pending);
        }
        else
        {
            _txPool.GetPendingTransactionsBySender(TestItem.AddressA).Returns(pending);
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(5UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(expectedNonce));
        }
    }

    [Test]
    public void should_reuse_nonce_if_tx_rejected()
    {
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (_nonceManager.TxWithNonceReceived(TestItem.AddressA, 1)) { }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }
    }

    [Test]
    [Repeat(2)]
    public void should_lock_on_same_account()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong _);
        });
        task.Wait(TimeSpan.FromMilliseconds(1_000));
        Assert.That(task.IsCompleted, Is.EqualTo(false));
    }

    [Test]
    [Repeat(3)]
    public void should_not_lock_on_different_accounts()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _txPool, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Factory.StartNew(() =>
        {
            using NonceLocker locker2 = _nonceManager.ReserveNonce(TestItem.AddressB, _txPool, out ulong nonce2);
            Assert.That(nonce2, Is.EqualTo(0UL));
        }, TaskCreationOptions.LongRunning);
        Assert.That(task.Wait(TimeSpan.FromMilliseconds(10_000)), Is.True);
    }
}
