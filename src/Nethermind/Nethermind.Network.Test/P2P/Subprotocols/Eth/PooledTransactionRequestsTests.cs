// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

public class PooledTransactionRequestsTests
{
    [Test]
    public void Snapshot_survives_source_disposal_and_tracker_disposal([Values(0, 1, 16, 17, 256)] int count)
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(2, pool);
        ValueHash256[] hashes = new ValueHash256[count];
        hashes.AsSpan().Fill(TestItem.KeccakA.ValueHash256);
        requests.Add(1, hashes);
        hashes.AsSpan().Clear();

        Assert.That(requests.TryClaim(1, out PooledTransactionRequests.Request request), Is.True);
        using (request)
        {
            requests.Dispose();
            Assert.That(pool.Count, Is.EqualTo(1));
            Assert.That(request.Hashes.Length, Is.EqualTo(count));
            foreach (ValueHash256 hash in request.Hashes) Assert.That(hash, Is.EqualTo(TestItem.KeccakA.ValueHash256));
            Assert.That(requests.TryClaim(1, out _), Is.False);
        }
        Assert.That(pool.Count, Is.Zero);
        requests.Add(2, hashes);
        Assert.That(requests.TryClaim(2, out _), Is.False);
        Assert.That(pool.Count, Is.Zero);
    }

    [Test]
    public void Replacing_and_evicting_returns_snapshots_and_preserves_fifo()
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(2, pool);
        requests.Add(1, [TestItem.KeccakA.ValueHash256]);
        requests.Add(2, [TestItem.KeccakA.ValueHash256]);
        requests.Add(1, [TestItem.KeccakB.ValueHash256]);
        requests.Add(3, [TestItem.KeccakC.ValueHash256]);
        Assert.That(pool.Count, Is.EqualTo(2));
        Assert.That(requests.TryClaim(2, out _), Is.False);
        Assert.That(requests.TryClaim(1, out PooledTransactionRequests.Request request), Is.True);
        using (request) Assert.That(request.Hashes[0], Is.EqualTo(TestItem.KeccakB.ValueHash256));
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
                expected.Remove(id);
                if (expected.Count == capacity) expected.RemoveAt(0);
                expected.Add(id);
                requests.Add(id, [TestItem.KeccakA.ValueHash256]);
            }
            else
            {
                bool claimed = requests.TryClaim(id, out PooledTransactionRequests.Request request);
                Assert.That(claimed, Is.EqualTo(expected.Remove(id)));
                if (claimed) request.Dispose();
            }
        }
    }

    [Test]
    public void Concurrent_claim_eviction_and_disposal_return_each_rental_once()
    {
        TrackingPool pool = new();
        using PooledTransactionRequests requests = new(16, pool);
        Parallel.For(0, 10000, i =>
        {
            requests.Add(i, [TestItem.KeccakA.ValueHash256]);
            if (requests.TryClaim(i, out PooledTransactionRequests.Request request))
            {
                using (request) Assert.That(request.Hashes[0], Is.EqualTo(TestItem.KeccakA.ValueHash256));
            }
            if (i == 5000) requests.Dispose();
        });
        requests.Dispose();
        Assert.That(pool.Count, Is.Zero);
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
