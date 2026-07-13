// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleLeaseTests
{
    private static ReadOnlySnapshotBundle EmptyReadOnlyBundle() =>
        new(new SnapshotPooledList(0), Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty());

    private static SnapshotBundle NewBundle(IResourcePool pool) =>
        new(EmptyReadOnlyBundle(), new NullTrieNodeCache(), pool, ResourcePool.Usage.MainBlockProcessing);

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }

    private sealed class CountingPool(IResourcePool inner) : IResourcePool
    {
        public int SnapshotContentReturns;
        public int CachedResourceReturns;

        public SnapshotContent GetSnapshotContent(ResourcePool.Usage usage) => inner.GetSnapshotContent(usage);

        public void ReturnSnapshotContent(ResourcePool.Usage usage, SnapshotContent snapshotContent)
        {
            Interlocked.Increment(ref SnapshotContentReturns);
            inner.ReturnSnapshotContent(usage, snapshotContent);
        }

        public SortedSnapshotContent GetSortedSnapshotContent(ResourcePool.Usage usage) => inner.GetSortedSnapshotContent(usage);

        public void ReturnSortedSnapshotContent(ResourcePool.Usage usage, SortedSnapshotContent sortedContent) =>
            inner.ReturnSortedSnapshotContent(usage, sortedContent);

        public TransientResource GetCachedResource(ResourcePool.Usage usage) => inner.GetCachedResource(usage);

        public void ReturnCachedResource(ResourcePool.Usage usage, TransientResource transientResource)
        {
            Interlocked.Increment(ref CachedResourceReturns);
            inner.ReturnCachedResource(usage, transientResource);
        }

        public Snapshot CreateSnapshot(in StateId from, in StateId to, ResourcePool.Usage usage) =>
            inner.CreateSnapshot(from, to, usage);
    }

    [Test]
    public void TryLease_after_final_release_fails()
    {
        SnapshotBundle bundle = NewBundle(new ResourcePool(new FlatDbConfig { CompactSize = 2 }));

        bundle.Dispose();

        Assert.That(bundle.TryLease(), Is.False);
    }

    [Test]
    public void Owner_dispose_defers_pool_returns_until_last_lease_and_runs_them_once()
    {
        CountingPool pool = new(new ResourcePool(new FlatDbConfig { CompactSize = 2 }));
        SnapshotBundle bundle = NewBundle(pool);
        bundle.SetAccount(TestItem.AddressA, new Account(42));

        Assert.That(bundle.TryLease(), Is.True);

        bundle.Dispose();
        bundle.Dispose();

        Assert.That(pool.SnapshotContentReturns, Is.Zero);
        Assert.That(pool.CachedResourceReturns, Is.Zero);
        Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(new Account(42)));

        bundle.ReleaseLease();

        Assert.That(pool.SnapshotContentReturns, Is.EqualTo(1));
        Assert.That(pool.CachedResourceReturns, Is.EqualTo(1));
        Assert.That(bundle.TryLease(), Is.False);
    }

    [Test]
    public void Retired_transient_resource_returns_once_after_manager_and_warmer_release()
    {
        CountingPool pool = new(new ResourcePool(new FlatDbConfig { CompactSize = 2 }));
        SnapshotBundle bundle = NewBundle(pool);

        (Snapshot? snapshot, TransientResource? retired) =
            bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));

        Assert.That(retired!.TryAcquireLease(), Is.True);

        retired.ReleaseLease();
        Assert.That(pool.CachedResourceReturns, Is.Zero);

        retired.ReleaseLease();
        Assert.That(pool.CachedResourceReturns, Is.EqualTo(1));
        Assert.That(retired.TryAcquireLease(), Is.False);

        snapshot!.Dispose();
        bundle.Dispose();

        Assert.That(pool.CachedResourceReturns, Is.EqualTo(2));
        Assert.That(pool.SnapshotContentReturns, Is.EqualTo(2));
    }

    // Regression for the trie-warmer recycle-under-reader race: a warmer-shaped reader holding
    // TryLease must never observe missing or foreign values while the owning scope disposes the
    // bundle and the pool recycles its contents into subsequent bundles.
    [Test]
    public void Leased_warmer_reads_see_no_false_misses_or_foreign_values_under_pool_churn()
    {
        const int keyCount = 128;

        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address[] addresses = TestItem.Addresses.AsSpan(0, keyCount).ToArray();
        TreePath warmPath = TreePath.FromNibble([1, 2, 3]);

        Published? published = null;
        long anomalies = 0;
        Exception? readerException = null;
        bool stop = false;

        Task[] readers = new Task[Math.Max(2, Environment.ProcessorCount - 2)];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        Published? p = Volatile.Read(ref published);
                        if (p is null || !p.Bundle.TryLease()) continue;
                        try
                        {
                            foreach (Address address in addresses)
                            {
                                Account? account = p.Bundle.GetAccount(address);
                                if (account is null || account.Balance != p.Epoch)
                                {
                                    Interlocked.Increment(ref anomalies);
                                }
                            }

                            p.Bundle.FindStateNodeOrUnknownForTrieWarmer(warmPath, TestItem.KeccakB);
                        }
                        finally
                        {
                            p.Bundle.ReleaseLease();
                        }
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref readerException, e, null);
                    Volatile.Write(ref stop, true);
                }
            });
        }

        Stopwatch sw = Stopwatch.StartNew();
        int epochs = 0;
        SnapshotBundle? previous = null;
        for (ulong e = 1; sw.ElapsedMilliseconds < 3_000 && !Volatile.Read(ref stop); e++, epochs++)
        {
            SnapshotBundle bundle = NewBundle(pool);
            foreach (Address address in addresses)
            {
                bundle.SetAccount(address, new Account((UInt256)e));
            }

            (Snapshot? snapshot, TransientResource? retired) =
                bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(e, TestItem.KeccakA));
            retired!.ReleaseLease();
            snapshot!.Dispose();

            Volatile.Write(ref published, new Published(bundle, (UInt256)e));
            previous?.Dispose();
            previous = bundle;
        }

        Volatile.Write(ref stop, true);
        Task.WaitAll(readers);
        previous?.Dispose();

        TestContext.Out.WriteLine($"epochs={epochs} anomalies={Volatile.Read(ref anomalies)}");
        Assert.That(readerException, Is.Null);
        Assert.That(Volatile.Read(ref anomalies), Is.Zero);
    }

    private sealed record Published(SnapshotBundle Bundle, UInt256 Epoch);
}
