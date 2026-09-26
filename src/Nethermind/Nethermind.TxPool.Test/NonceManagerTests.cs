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
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
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
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
            locker.Accept();
            nonces.Enqueue(nonce);
        });

        Assert.That(result.IsCompleted, Is.True);
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
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

        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(10UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
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

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 2))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(3UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(5UL));
            locker.Accept();
        }
    }

    [Test]
    public void TxWithNonceReceived_should_drop_senders_once_their_nonces_are_confirmed()
    {
        IAccountStateProvider accounts = Substitute.For<IAccountStateProvider>();
        NonceManager nonceManager = new(accounts, minSweepThreshold: 4);
        for (int i = 0; i < 4; i++)
        {
            using NonceLocker locker = nonceManager.TxWithNonceReceived(TestItem.Addresses[i], 0);
            locker.Accept();
        }

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(4), "precondition: every raw-tx sender is tracked");
        accounts.GetNonce(Arg.Any<Address>()).Returns(1UL);

        using (NonceLocker locker = nonceManager.TxWithNonceReceived(TestItem.Addresses[4], 0))
        {
            locker.Accept();
        }

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(1), "senders whose nonces are all confirmed must be dropped, leaving only the new one");
    }

    // 1. A sends a raw tx with nonce 1 while its account nonce is 0, so nonce 1 is still pending.
    // 2. Three other senders confirm their raw txs and a fifth sender triggers the sweep.
    // 3. A's managed reservations must still step over nonce 1: 0, then 2.
    [Test]
    public void TxWithNonceReceived_should_keep_a_sender_with_a_pending_nonce_through_a_sweep()
    {
        IAccountStateProvider accounts = Substitute.For<IAccountStateProvider>();
        NonceManager nonceManager = new(accounts, minSweepThreshold: 4);
        using (NonceLocker locker = nonceManager.TxWithNonceReceived(TestItem.AddressA, 1))
        {
            locker.Accept();
        }

        for (int i = 1; i < 4; i++)
        {
            using NonceLocker locker = nonceManager.TxWithNonceReceived(TestItem.Addresses[i], 0);
            locker.Accept();
            accounts.GetNonce(TestItem.Addresses[i]).Returns(1UL);
        }

        using (NonceLocker locker = nonceManager.TxWithNonceReceived(TestItem.Addresses[4], 0))
        {
            locker.Accept();
        }

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(2), "precondition: only the sender with a pending nonce survives next to the new one");

        using (NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong first))
        {
            Assert.That(first, Is.EqualTo(0UL), "the account nonce is still free");
            locker.Accept();
        }

        using (nonceManager.ReserveNonce(TestItem.AddressA, out ulong second))
        {
            Assert.That(second, Is.EqualTo(2UL), "the pending raw nonce must still be skipped after the sweep");
        }
    }

    [Test]
    public void ReserveNonce_should_start_from_a_high_account_nonce_without_walking_up_to_it()
    {
        const ulong accountNonce = 1_000_000_000_000;
        IAccountStateProvider accounts = Substitute.For<IAccountStateProvider>();
        accounts.GetNonce(TestItem.AddressA).Returns(accountNonce);
        NonceManager nonceManager = new(accounts);

        using (nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(accountNonce), "a new sender starts at its account nonce");
        }
    }

    [Test]
    public void should_reuse_nonce_if_tx_rejected()
    {
        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (_nonceManager.TxWithNonceReceived(TestItem.AddressA, 1)) { }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }
    }

    [Test]
    [Repeat(2)]
    public void should_lock_on_same_account()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong _);
        });
        task.Wait(TimeSpan.FromMilliseconds(1_000));
        Assert.That(task.IsCompleted, Is.EqualTo(false));
    }

    [Test]
    [Repeat(3)]
    public void should_not_lock_on_different_accounts()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Factory.StartNew(() =>
        {
            using NonceLocker locker2 = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce2);
            Assert.That(nonce2, Is.EqualTo(0UL));
        }, TaskCreationOptions.LongRunning);
        Assert.That(task.Wait(TimeSpan.FromMilliseconds(10_000)), Is.True);
    }
}
