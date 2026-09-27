// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private IPendingTxsBySender _pendingTxs;
    private readonly ConcurrentDictionary<Hash256, Transaction> _pending = new();
    private readonly ConcurrentDictionary<Hash256, Transaction> _broadcastOnly = new();
    private int _hashSeed;

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
        _pendingTxs = Substitute.For<IPendingTxsBySender>();
        _pendingTxs.ContainsTx(Arg.Any<Hash256>(), Arg.Any<TxType>())
            .Returns(ci => _pending.ContainsKey(ci.ArgAt<Hash256>(0)) || _broadcastOnly.ContainsKey(ci.ArgAt<Hash256>(0)));
        _pendingTxs.GetPendingTransactionsBySender(Arg.Any<Address>()).Returns(ci => Pending(ci.ArgAt<Address>(0), blobs: false));
        _pendingTxs.GetPendingLightBlobTransactionsBySender(Arg.Any<Address>()).Returns(ci => Pending(ci.ArgAt<Address>(0), blobs: true));
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            Accept(locker, TestItem.AddressB, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            Accept(locker, TestItem.AddressB, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
            Accept(locker, TestItem.AddressB, nonce);
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
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            Accept(locker, TestItem.AddressA, nonce);
            nonces.Enqueue(nonce);
        });

        Assert.That(result.IsCompleted, Is.True);
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
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

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(10UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void ReserveNonce_should_release_the_account_lock_when_the_reservation_throws()
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(_ => throw new InvalidOperationException(), _ => 3UL);
        _nonceManager = new NonceManager(accountStateProvider);

        Assert.Throws<InvalidOperationException>(() => _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out _));

        Task<ulong> next = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            return nonce;
        });

        Assert.That(next.Wait(TimeSpan.FromSeconds(5)), Is.True, "a failed reservation must not keep the account locked");
        Assert.That(next.Result, Is.EqualTo(3UL));
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_if_TxWithNonceReceived()
    {
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 4))
        {
            Accept(locker, TestItem.AddressA, 4);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 2))
        {
            Accept(locker, TestItem.AddressA, 2);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(3UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(5UL));
            Accept(locker, TestItem.AddressA, nonce);
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
            Accept(locker, TestItem.AddressA, usedNonce);
        }

        _pending.Clear();

        Transaction[] pending = pendingNonces.Select(static n => Build.A.Transaction.WithNonce(n).TestObject).ToArray();
        if (blobPool)
        {
            _pendingTxs.GetPendingLightBlobTransactionsBySender(TestItem.AddressA).Returns(pending);
        }
        else
        {
            _pendingTxs.GetPendingTransactionsBySender(TestItem.AddressA).Returns(pending);
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(5UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(expectedNonce));
        }
    }

    [TestCase(false, false, 5UL, TestName = "ReserveNonce_should_reuse_evicted_nonce_after_skip_was_not_accepted")]
    [TestCase(false, true, 6UL, TestName = "ReserveNonce_should_skip_again_after_skip_was_not_accepted")]
    [TestCase(true, true, 7UL, TestName = "ReserveNonce_should_commit_skip_once_accepted")]
    public void ReserveNonce_should_commit_pending_skip_only_on_accept(bool acceptSkip, bool stillPending, ulong expectedNonce)
    {
        // 1. A raw tx with nonce 5 is accepted while the account nonce is 0.
        // 2. The account nonce catches up to 5 while tx 5 is still pending.
        // 3. A reservation skips 5 and gets 6, then is accepted or disposed.
        // 4. Tx 5 stays pending or is evicted, and the account nonce stays 5.
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        _nonceManager = new NonceManager(accountStateProvider);

        Transaction raw;
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 5))
        {
            raw = Accept(locker, TestItem.AddressA, 5);
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(5UL);
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong skippedNonce))
        {
            Assert.That(skippedNonce, Is.EqualTo(6UL), "precondition: nonce 5 is still pending so the reservation skips it");
            if (acceptSkip)
            {
                Accept(locker, TestItem.AddressA, skippedNonce);
            }
        }

        if (!stillPending)
        {
            _pending.TryRemove(raw.Hash!, out _);
        }

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(expectedNonce), "a reservation disposed without Accept must not take a nonce");
        }
    }

    [Test]
    public void ReserveNonce_should_reuse_nonce_after_its_accepted_tx_is_evicted([Values] bool managed)
    {
        Transaction accepted;
        if (managed)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            accepted = Accept(locker, TestItem.AddressA, nonce);
        }
        else
        {
            using NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 0);
            accepted = Accept(locker, TestItem.AddressA, 0);
        }

        _pending.TryRemove(accepted.Hash!, out _);

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void ReserveNonce_should_reuse_evicted_nonce_below_later_pending_ones()
    {
        Transaction[] accepted = new Transaction[3];
        for (int i = 0; i < accepted.Length; i++)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            accepted[i] = Accept(locker, TestItem.AddressA, nonce);
        }

        _pending.TryRemove(accepted[1].Hash!, out _);

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(3UL));
        }
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_held_only_by_persistent_broadcast()
    {
        Transaction accepted;
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            accepted = Accept(locker, TestItem.AddressA, nonce);
        }

        _pending.TryRemove(accepted.Hash!, out _);
        _broadcastOnly[accepted.Hash!] = accepted;

        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
        }
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_whose_tx_was_replaced_in_the_pool([Values] bool blobPool)
    {
        Transaction accepted;
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            accepted = Accept(locker, TestItem.AddressA, nonce);
        }

        _pending.TryRemove(accepted.Hash!, out _);
        Transaction replacement = BuildTx(TestItem.AddressA, accepted.Nonce, blobPool ? TxType.Blob : TxType.Legacy);
        _pending[replacement.Hash!] = replacement;

        for (int i = 0; i < 2; i++)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            Assert.That(nonce, Is.EqualTo(1UL));
        }

        _pendingTxs.Received(1).GetPendingTransactionsBySender(TestItem.AddressA);
    }

    [Test]
    public void ReserveNonce_should_skip_nonces_returned_to_the_pool_by_a_reorg()
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        _nonceManager = new NonceManager(accountStateProvider);

        for (int i = 0; i < 2; i++)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
            Accept(locker, TestItem.AddressA, nonce);
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(2UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL), "precondition: the mined nonces are released");
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
        }
    }

    [Test]
    public void ReserveNonce_should_not_hand_an_in_flight_nonce_to_a_concurrent_reservation()
    {
        using ManualResetEventSlim reserving = new();
        Task<ulong> concurrent;
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            concurrent = Task.Run(() =>
            {
                reserving.Set();
                using NonceLocker concurrentLocker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong concurrentNonce);
                Accept(concurrentLocker, TestItem.AddressA, concurrentNonce);
                return concurrentNonce;
            });
            Assert.That(reserving.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the concurrent reservation has started");
            Assert.That(concurrent.Wait(100), Is.False, "the concurrent reservation must wait for the in-flight one");
            Assert.That(nonce, Is.EqualTo(0UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        Assert.That(concurrent.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(concurrent.Result, Is.EqualTo(1UL));
    }

    [Test]
    public void ReserveNonce_should_read_account_nonce_after_waiting_for_the_account_lock()
    {
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(5UL);
        _nonceManager = new NonceManager(accountStateProvider);

        Transaction mined;
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 5))
        {
            mined = Accept(locker, TestItem.AddressA, 5);
        }

        ulong waitingNonce = 0;
        Thread waiting = new(() =>
        {
            using NonceLocker waitingLocker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out waitingNonce);
        });

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(6UL), "precondition: nonce 5 is still pending");
            waiting.Start();
            SpinWait.SpinUntil(() => waiting.ThreadState == ThreadState.WaitSleepJoin, TimeSpan.FromSeconds(10));

            accountStateProvider.GetNonce(TestItem.AddressA).Returns(6UL);
            _pending.TryRemove(mined.Hash!, out _);
            Accept(locker, TestItem.AddressA, nonce);
        }

        Assert.That(waiting.Join(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(waitingNonce, Is.EqualTo(7UL));
    }

    [Test]
    public void should_reuse_nonce_if_tx_rejected()
    {
        using (_nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            Accept(locker, TestItem.AddressA, nonce);
        }

        using (_nonceManager.TxWithNonceReceived(TestItem.AddressA, 1)) { }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            Accept(locker, TestItem.AddressA, nonce);
        }
    }

    [Test]
    [Repeat(2)]
    public void should_lock_on_same_account()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong _);
        });
        task.Wait(TimeSpan.FromMilliseconds(1_000));
        Assert.That(task.IsCompleted, Is.EqualTo(false));
    }

    [Test]
    [Repeat(3)]
    public void should_not_lock_on_different_accounts()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, _pendingTxs, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Factory.StartNew(() =>
        {
            using NonceLocker locker2 = _nonceManager.ReserveNonce(TestItem.AddressB, _pendingTxs, out ulong nonce2);
            Assert.That(nonce2, Is.EqualTo(0UL));
        }, TaskCreationOptions.LongRunning);
        Assert.That(task.Wait(TimeSpan.FromMilliseconds(10_000)), Is.True);
    }

    private Transaction Accept(NonceLocker locker, Address sender, ulong nonce)
    {
        Transaction transaction = BuildTx(sender, nonce, TxType.Legacy);
        _pending[transaction.Hash!] = transaction;
        locker.Accept(transaction);
        return transaction;
    }

    private Transaction BuildTx(Address sender, ulong nonce, TxType type) =>
        Build.A.Transaction
            .WithType(type)
            .WithNonce(nonce)
            .WithSenderAddress(sender)
            .WithHash(Keccak.Compute(Interlocked.Increment(ref _hashSeed).ToString()))
            .TestObject;

    private Transaction[] Pending(Address sender, bool blobs) =>
        _pending
            .Select(static entry => entry.Value)
            .Where(t => t.SenderAddress == sender && (t.Type == TxType.Blob) == blobs)
            .OrderBy(static t => t.Nonce)
            .ToArray();
}
