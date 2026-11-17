// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.TxPool;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

public class PooledTransactionRequestsTests
{
    [Test]
    public void Claimed_response_preserves_send_time_tokens_in_returned_transaction_order()
    {
        ValueHash256 firstHash = TestItem.KeccakA.ValueHash256;
        ValueHash256 secondHash = TestItem.KeccakB.ValueHash256;
        ValueHash256 omittedHash = TestItem.KeccakC.ValueHash256;
        RetryRequestToken<ValueHash256>[] tokens = [new(firstHash, 1, 2, 3), new(omittedHash, 7, 8, 9), new(secondHash, 4, 5, 6)];
        using PooledTransactionRequests requests = new(32);
        Assert.That(requests.TryAdd(1, [firstHash, omittedHash, secondHash], tokens: tokens), Is.True);
        tokens.AsSpan().Clear();
        Assert.That(requests.TryClaim(1, out PooledTransactionRequests.Request? request), Is.True);
        PooledTransactionRequests.ResponseContext context;
        using (request)
            context = request!.CreateResponseContext([new Transaction { Hash = firstHash.ToHash256() }, new Transaction { Hash = secondHash.ToHash256() }]);
        requests.Dispose();
        using (context)
            Assert.That(context.Tokens, Is.EqualTo(new RetryRequestToken<ValueHash256>[]
            {
                new(firstHash, 1, 2, 3), new(secondHash, 4, 5, 6)
            }));
        context.Dispose();
    }

    [Test]
    public void Unused_tracker_can_be_disposed_before_first_request()
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(2048, pool);
        Assert.That(requests.TryClaim(1, out _), Is.False);
        requests.Dispose();
        requests.TryAdd(1, [TestItem.KeccakA.ValueHash256]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests.TryClaim(1, out _), Is.False);
#pragma warning restore NUnit2045
            Assert.That(pool.Count, Is.Zero);
        }
    }

    [Test]
    public void Snapshot_survives_source_disposal_and_tracker_disposal([Values(0, 1, 16, 17, 256)] int count)
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(2, pool);
        ValueHash256[] hashes = new ValueHash256[count];
        hashes.AsSpan().Fill(TestItem.KeccakA.ValueHash256);
        requests.TryAdd(1, hashes);
        hashes.AsSpan().Clear();

        Assert.That(requests.TryClaim(1, out PooledTransactionRequests.Request? request), Is.True);
        using (request)
        {
            requests.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.Count, Is.EqualTo(1));
                Assert.That(request.Hashes.Length, Is.EqualTo(count));
            }
            foreach (ValueHash256 hash in request.Hashes) Assert.That(hash, Is.EqualTo(TestItem.KeccakA.ValueHash256));
            Assert.That(requests.TryClaim(1, out _), Is.False);
        }
        Assert.That(pool.Count, Is.Zero);
        requests.TryAdd(2, hashes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests.TryClaim(2, out _), Is.False);
#pragma warning restore NUnit2045
            Assert.That(pool.Count, Is.Zero);
        }
    }

    [Test]
    public void Duplicate_and_full_requests_preserve_unanswered_snapshots()
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(2, pool);
        PooledTransactionRequests.Request? request;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests.TryAdd(1, [TestItem.KeccakA.ValueHash256]), Is.True);
            Assert.That(requests.TryAdd(2, [TestItem.KeccakA.ValueHash256]), Is.True);
            Assert.That(requests.TryAdd(1, [TestItem.KeccakB.ValueHash256]), Is.False);
            Assert.That(requests.TryAdd(3, [TestItem.KeccakC.ValueHash256]), Is.False);
            Assert.That(pool.Count, Is.EqualTo(2));
            Assert.That(requests.TryClaim(1, out request), Is.True);
        }
        using (request) Assert.That(request!.Hashes[0], Is.EqualTo(TestItem.KeccakA.ValueHash256));
        Assert.That(requests.TryClaim(2, out PooledTransactionRequests.Request? second), Is.True);
        second!.Dispose();
        requests.Dispose();
        requests.Dispose();
        Assert.That(pool.Count, Is.Zero);
    }

    [Test]
    public void Slot_reuse_matches_bounded_fifo([Values(1, 2, 17)] int capacity)
    {
        using PooledTransactionRequests requests = new(capacity);
        List<long> expected = [];
        Random random = new(42);
        for (int i = 0; i < 10000; i++)
        {
            long id = random.Next(capacity * 2);
            if (random.Next(2) == 0)
            {
                bool accepted = !expected.Contains(id) && expected.Count < capacity;
                Assert.That(requests.TryAdd(id, [TestItem.KeccakA.ValueHash256]), Is.EqualTo(accepted));
                if (accepted) expected.Add(id);
            }
            else
            {
                bool claimed = requests.TryClaim(id, out PooledTransactionRequests.Request? request);
                Assert.That(claimed, Is.EqualTo(expected.Remove(id)));
                if (claimed) request!.Dispose();
            }
        }
    }

    [Test]
    public void Concurrent_claim_refusal_and_disposal_return_each_rental_once()
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(16, pool);
        Parallel.For(0, 10000, i =>
        {
            requests.TryAdd(i, [TestItem.KeccakA.ValueHash256]);
            if (requests.TryClaim(i, out PooledTransactionRequests.Request? request))
            {
                using (request) Assert.That(request.Hashes[0], Is.EqualTo(TestItem.KeccakA.ValueHash256));
            }
            if (i == 5000) requests.Dispose();
        });
        requests.Dispose();
        Assert.That(pool.Count, Is.Zero);
    }

    [Test]
    public void Expiry_releases_unanswered_reservations_but_preserves_a_claimed_response([Values] bool claimed)
    {
        ManualTimeProvider clock = new();
        TrackingPool pool = new();
        PeerWorkQuota quota = new(8192);
        using PooledTransactionRequests requests = new(32, pool, clock);
        for (int index = 0; index < 32; index++)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                out PeerWorkQuota.Reservation? reservation), Is.True);
            using (reservation)
                Assert.That(requests.TryAdd(index, [TestItem.KeccakA.ValueHash256], reservation), Is.True);
        }
        PooledTransactionRequests.Request? snapshot = null;
        if (claimed) Assert.That(requests.TryClaim(0, out snapshot), Is.True);
        try
        {
            clock.AdvanceAndFireTimer(Timeouts.Eth - TimeSpan.FromTicks(1));
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
            clock.AdvanceAndFireTimer(TimeSpan.FromTicks(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.Count, Is.EqualTo(claimed ? 1 : 0));
                if (claimed) Assert.That(snapshot!.Hashes[0], Is.EqualTo(TestItem.KeccakA.ValueHash256));
            }

            List<PeerWorkQuota.Reservation> recovered = [];
            try
            {
                for (int index = 0; index < (claimed ? 31 : 32); index++)
                {
                    Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                        out PeerWorkQuota.Reservation? reservation), Is.True);
                    recovered.Add(reservation!);
                }
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
                if (claimed)
                {
                    snapshot!.Dispose();
                    Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                        out PeerWorkQuota.Reservation? reservation), Is.True);
                    recovered.Add(reservation!);
                }
            }
            finally
            {
                foreach (PeerWorkQuota.Reservation reservation in recovered) reservation.Dispose();
            }
        }
        finally { snapshot?.Dispose(); }
        Assert.That(pool.Count, Is.Zero);
    }

    [Test]
    public void Throwing_completion_retires_every_snapshot_and_claim([Values(0, 1, 2, 3)] int path)
    {
        ManualTimeProvider timer = new();
        ManualTimestamper clock = new(timer.GetUtcNow().UtcDateTime);
        PacketReceiptTracker receipts = new();
        receipts.BindClock(clock);
        TrackingPool pool = new();
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        using PooledTransactionRequests requests = new(3, pool, timer, new ILogger(logger));
        int completed = 0;
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            PooledTransactionRequests.RequestLifecycle lifecycle = new(receipts, clock, _ =>
            {
                completed++;
                if (index == (path == 1 ? 2 : 0)) throw new InvalidOperationException("Local completion failed.");
            });
            Assert.That(requests.TryAdd(i, [TestItem.KeccakA.ValueHash256], lifecycle: lifecycle), Is.True);
            if (path != 1 || i != 2) requests.ConfirmSent(i);
        }
        clock.Add(Timeouts.Eth);
        switch (path)
        {
            case 0:
                Assert.DoesNotThrow(() => requests.ExpirePending());
                break;
            case 1:
                Assert.DoesNotThrow(() =>
                {
                    Assert.That(requests.TryClaim(2, out PooledTransactionRequests.Request? claimed), Is.True);
                    claimed!.Dispose();
                });
                break;
            case 2:
                Assert.DoesNotThrow(() => requests.Dispose());
                break;
            case 3:
                Assert.DoesNotThrow(() => timer.AdvanceAndFireTimer(Timeouts.Eth));
                break;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(3));
            Assert.That(pool.Count, Is.Zero);
            Assert.That(requests.TryClaim(2, out _), Is.False);
            logger.Received(1).Error(Arg.Any<string>(), Arg.Any<Exception>());
        }
    }

    [Test]
    public void Throwing_expiry_during_registration_does_not_strand_an_unsent_snapshot()
    {
        ManualTimeProvider timer = new();
        ManualTimestamper clock = new(timer.GetUtcNow().UtcDateTime);
        PacketReceiptTracker receipts = new();
        receipts.BindClock(clock);
        TrackingPool pool = new();
        PeerWorkQuota quota = new(8192);
        using PooledTransactionRequests requests = new(32, pool, timer);
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
            out PeerWorkQuota.Reservation? original), Is.True);
        using (original)
            Assert.That(requests.TryAdd(1, [TestItem.KeccakA.ValueHash256], original,
                new(receipts, clock, _ => throw new InvalidOperationException("Local expiry failed."))), Is.True);
        requests.ConfirmSent(1);
        clock.Add(Timeouts.Eth);
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
            out PeerWorkQuota.Reservation? next), Is.True);
        using (next)
            Assert.DoesNotThrow(() => Assert.That(requests.TryAdd(2, [TestItem.KeccakB.ValueHash256], next,
                new(receipts, clock, _ => { })), Is.True));
        Assert.That(pool.Count, Is.EqualTo(1));
        requests.Cancel(2);
        Assert.That(pool.Count, Is.Zero);
        AssertPooledReplyQuotaRecovered(quota);
    }

    [Test]
    public void Timer_boundary_contains_clock_failure_and_recovers_the_snapshot([Values] bool claim)
    {
        ManualTimeProvider timer = new();
        ThrowingTimestampProvider clock = new(timer);
        ManualTimestamper lifecycleClock = new(timer.GetUtcNow().UtcDateTime);
        PacketReceiptTracker receipts = new();
        receipts.BindClock(lifecycleClock);
        TrackingPool pool = new();
        PeerWorkQuota quota = new(8192);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        using PooledTransactionRequests requests = new(1, pool, clock, new ILogger(logger));
        int completed = 0;
        PooledTransactionRequests.RequestLifecycle? lifecycle = claim ? new(receipts, lifecycleClock, unanswered =>
        {
            Assert.That(unanswered, Is.False);
            completed++;
        }) : null;
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
            out PeerWorkQuota.Reservation? original), Is.True);
        using (original)
            Assert.That(requests.TryAdd(1, [TestItem.KeccakA.ValueHash256], original, lifecycle), Is.True);
        requests.ConfirmSent(1);
        clock.Throw = true;
        if (claim)
            Assert.Throws<InvalidOperationException>(() => requests.TryClaim(1, out _, lifecycleClock.UtcNowOffset));
        else Assert.DoesNotThrow(() => timer.AdvanceAndFireTimer(Timeouts.Eth));
        using (Assert.EnterMultipleScope())
        {
            logger.Received(claim ? 0 : 1).Error(Arg.Any<string>(), Arg.Any<Exception>());
            Assert.That(pool.Count, Is.EqualTo(claim ? 0 : 1));
            Assert.That(completed, Is.EqualTo(claim ? 1 : 0));
        }
        clock.Throw = false;
        requests.ExpirePending();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Count, Is.Zero);
            Assert.That(requests.TryClaim(1, out _), Is.False);
        }
        AssertPooledReplyQuotaRecovered(quota);
    }

    [Test]
    public void Partial_expiry_clock_failure_retires_already_removed_snapshots([Values(0, 1, 2, 3)] int path)
    {
        ManualTimeProvider timer = new();
        ManualTimestamper clock = new(timer.GetUtcNow().UtcDateTime);
        ITimestamper faulty = Substitute.For<ITimestamper>();
        bool fault = false;
        faulty.UtcNowOffset.Returns(_ => fault ? throw new InvalidOperationException("Local expiry clock failed.") : clock.UtcNowOffset);
        PacketReceiptTracker receipts = new();
        receipts.BindClock(clock);
        TrackingPool pool = new();
        PeerWorkQuota quota = new(8192);
        using PooledTransactionRequests requests = new(2, pool, timer);
        int expired = 0;
        int canceled = 0;
        for (int i = 1; i <= 2; i++)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                out PeerWorkQuota.Reservation? original), Is.True);
            using (original)
                Assert.That(requests.TryAdd(i, [TestItem.KeccakA.ValueHash256], original,
                    new(receipts, i == 1 ? clock : faulty, unanswered =>
                    {
                        if (unanswered) expired++;
                        else canceled++;
                    })), Is.True);
            if (path == 3 && i == 2)
            {
                fault = true;
                Assert.Throws<InvalidOperationException>(() => requests.ConfirmSent(i));
                fault = false;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(canceled, Is.EqualTo(1));
                    Assert.That(pool.Count, Is.EqualTo(1));
                }
            }
            else requests.ConfirmSent(i);
        }
        clock.Add(Timeouts.Eth);
        fault = path != 3;
        if (path == 3) requests.ExpirePending();
        else Assert.Throws<InvalidOperationException>(() =>
        {
            switch (path)
            {
                case 0: requests.ExpirePending(); break;
                case 1: requests.TryAdd(3, [TestItem.KeccakB.ValueHash256]); break;
                case 2: requests.TryClaim(99, out _); break;
            }
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(canceled, Is.EqualTo(path == 3 ? 1 : 0));
            Assert.That(pool.Count, Is.EqualTo(path == 3 ? 0 : 1));
        }
        fault = false;
        requests.Cancel(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(canceled, Is.EqualTo(1));
            Assert.That(pool.Count, Is.Zero);
            Assert.That(requests.TryClaim(1, out _), Is.False);
        }
        AssertPooledReplyQuotaRecovered(quota);
    }

    private static void AssertPooledReplyQuotaRecovered(PeerWorkQuota quota)
    {
        List<PeerWorkQuota.Reservation> recovered = [];
        try
        {
            for (int i = 0; i < 32; i++)
            {
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                    out PeerWorkQuota.Reservation? reservation), Is.True);
                recovered.Add(reservation!);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        }
        finally { foreach (PeerWorkQuota.Reservation reservation in recovered) reservation.Dispose(); }
    }

    private sealed class ThrowingTimestampProvider(TimeProvider clock) : TimeProvider
    {
        public bool Throw { get; set; }
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => Throw ? throw new InvalidOperationException("Local clock failed.") : clock.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => clock.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class TrackingPool : ArrayPool<ValueHash256>
    {
        private readonly HashSet<ValueHash256[]> _rented = [];
        public int Count => _rented.Count;

        public override ValueHash256[] Rent(int minimumLength)
        {
            ValueHash256[] array = new ValueHash256[minimumLength + 3];
            lock (_rented) _rented.Add(array);
            return array;
        }

        public override void Return(ValueHash256[] array, bool clearArray = false)
        {
            lock (_rented) Assert.That(_rented.Remove(array), Is.True, "Buffer returned twice");
            array.AsSpan().Clear();
        }
    }
}
