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
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private const int ChurnMilliseconds = 2_000;
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);

    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig());

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public int TryGetCount;

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            TryGetCount++;
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }

    private SnapshotBundle NewBundle(Action<SnapshotContent>? persisted = null) =>
        new(FlatTestHelpers.MakeBundle(_pool, persisted), new NullTrieNodeCache(), _pool, ResourcePool.Usage.MainBlockProcessing);

    private static TrieNode Leaf(byte value) => new(NodeType.Leaf, new byte[] { 0xC1, value });

    // The trie warmer never reads the recyclable _snapshots; it reads persistence plus the _transientResource
    // node cache (which it also warms), pinned by a per-read lease. A node that lives only in the bundle's own
    // committed snapshot list is visible to a normal read but Unknown to the warmer; a node in persistence is
    // returned by both.
    [Test]
    public void Trie_warmer_reads_persistence_only_and_ignores_recyclable_snapshots()
    {
        Hash256 storageAddress = TestItem.KeccakC;
        TreePath persistedPath = TreePath.FromHexString("ab");
        TreePath persistedStoragePath = TreePath.FromHexString("cd");
        TrieNode persistedNode = Leaf(0x80);
        TrieNode persistedStorageNode = Leaf(0x81);

        using SnapshotBundle bundle = NewBundle(content =>
        {
            content.StateNodes[persistedPath] = persistedNode;
            content.StorageNodes[(storageAddress, persistedStoragePath)] = persistedStorageNode;
        });

        TreePath committedPath = TreePath.FromHexString("12");
        TreePath committedStoragePath = TreePath.FromHexString("34");
        TrieNode committedNode = Leaf(0x82);
        TrieNode committedStorageNode = Leaf(0x83);
        bundle.SetStateNode(committedPath, committedNode);
        bundle.SetStorageNode(storageAddress, committedStoragePath, committedStorageNode);

        // Commit folds the written nodes into the bundle's recyclable _snapshots and swaps the transient away
        // from them, so afterwards they are reachable only through the path the warmer must not take.
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), returnSnapshot: false);

        TrieNode warmedCommittedState = bundle.FindStateNodeOrUnknownForTrieWarmer(committedPath, TestItem.KeccakA);
        TrieNode warmedCommittedStorage = bundle.FindStorageNodeOrUnknownTrieWarmer(storageAddress, committedStoragePath, TestItem.KeccakA);
        TrieNode warmedPersistedState = bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakB);
        TrieNode warmedPersistedStorage = bundle.FindStorageNodeOrUnknownTrieWarmer(storageAddress, persistedStoragePath, TestItem.KeccakB);

        TrieNode normalStateRead = bundle.FindStateNodeOrUnknown(committedPath, TestItem.KeccakA);
        TrieNode normalStorageRead = bundle.FindStorageNodeOrUnknown(storageAddress, committedStoragePath, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmedCommittedState.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(warmedCommittedStorage.NodeType, Is.EqualTo(NodeType.Unknown));

            Assert.That(warmedPersistedState, Is.SameAs(persistedNode));
            Assert.That(warmedPersistedStorage, Is.SameAs(persistedStorageNode));

            Assert.That(normalStateRead, Is.SameAs(committedNode));
            Assert.That(normalStorageRead, Is.SameAs(committedStorageNode));
        }
    }

    [Test]
    public void Promoted_warmer_miss_does_not_poison_a_later_live_read()
    {
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakA;

        TrieNode warmed = bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        Assert.That(warmed.NodeType, Is.EqualTo(NodeType.Unknown));

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        cache.Add(retired!);
        retired!.ReleaseLease();

        TrieNode realNode = Leaf(0x99);
        bundle.SetStateNode(path, realNode);
        bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakB), returnSnapshot: false);

        TrieNode read = bundle.FindStateNodeOrUnknown(path, hash);
        Assert.That(read, Is.SameAs(realNode));
    }

    // The regression this PR fixes is a +3-5% AVG slowdown from #12793 dropping the warmer's negative cache.
    // Pin that the cache exists: a second warmer visit to the same missing path must be served from the
    // transient sentinel and must not re-probe the persistence-backed lookup a second time.
    [Test]
    public void Repeated_warmer_miss_is_served_from_the_negative_cache()
    {
        NullTrieNodeCache cache = new();
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakA;

        bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);

        Assert.That(cache.TryGetCount, Is.EqualTo(1));
    }

    // Dispose releases the transient back to the pool while warmer jobs may still be in flight. A read that
    // arrives after that must fall back to the leased persistence reader rather than touch the recycled
    // resource - and must not spin waiting for a transient that will never be replaced, which is what the
    // bounded wait here asserts.
    [Test]
    public void Trie_warmer_read_on_disposed_bundle_falls_back_to_persistence()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");
        TrieNode persistedNode = Leaf(0x80);

        SnapshotBundle bundle = NewBundle(content => content.StateNodes[persistedPath] = persistedNode);

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();

            Task<(TrieNode Node, bool ShouldPrewarm)> read = Task.Run(() =>
                (bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakA),
                    bundle.ShouldQueuePrewarm(TestItem.AddressA)));

            Assert.That(read.Wait(BailOutTimeout), Is.True, "the warmer read did not return on a disposed bundle");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.Result.Node, Is.SameAs(persistedNode));
                Assert.That(read.Result.ShouldPrewarm, Is.False);
            }
        }
        finally
        {
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    private sealed record ChurnEpoch(SnapshotBundle Bundle, TrieNode Node);

    // Regression for the warmer recycle-under-reader race. Warmer-shaped readers hold only a
    // ReadOnlySnapshotBundle lease (as the real warmer job does) while the owner drives both recycle paths:
    // CollectAndApplySnapshot, which swaps the transient in place and retires the old one to the pool while
    // the bundle stays published, and Dispose, which releases the transient outright. Each epoch owns a
    // distinct persisted node instance, so a read served from another epoch's recycled transient is caught by
    // identity rather than by value. The prewarm dedupe bloom lives on the same resource, so it is exercised
    // here too: unpinned, its BloomFilter can be freed by a pool overflow mid-read.
    [Test]
    public void Trie_warmer_reads_survive_owner_churn_without_foreign_values()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");

        ChurnEpoch NewEpoch()
        {
            TrieNode node = Leaf(0x80);
            return new ChurnEpoch(NewBundle(content => content.StateNodes[persistedPath] = node), node);
        }

        long leasedReads = 0;
        long queuedPrewarms = 0;
        long foreignReads = 0;
        long slotCounter = 0;
        Exception? readerException = null;
        bool stop = false;
        ChurnEpoch published = NewEpoch();

        Task[] readers = new Task[Math.Max(2, Environment.ProcessorCount - 2)];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        ChurnEpoch epoch = Volatile.Read(ref published);
                        if (!epoch.Bundle.TryLeaseReadOnlyBundle()) continue;
                        try
                        {
                            TrieNode node = epoch.Bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakA);
                            if (!ReferenceEquals(node, epoch.Node)) Interlocked.Increment(ref foreignReads);
                            Interlocked.Increment(ref leasedReads);

                            UInt256 slot = (UInt256)Interlocked.Increment(ref slotCounter);
                            if (epoch.Bundle.ShouldQueuePrewarm(TestItem.AddressA, slot)) Interlocked.Increment(ref queuedPrewarms);
                        }
                        finally
                        {
                            epoch.Bundle.ReleaseReadOnlyBundleLease();
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
        int commits = 0;
        int disposals = 0;
        ulong blockNumber = 0;
        ChurnEpoch current = published;
        while (sw.ElapsedMilliseconds < ChurnMilliseconds && !Volatile.Read(ref stop))
        {
            StateId from = new(blockNumber, TestItem.KeccakA);
            StateId to = new(++blockNumber, TestItem.KeccakA);
            current.Bundle.CollectAndApplySnapshot(from, to, returnSnapshot: false);
            commits++;

            ChurnEpoch next = NewEpoch();
            Volatile.Write(ref published, next);
            current.Bundle.Dispose();
            current = next;
            disposals++;
        }

        Volatile.Write(ref stop, true);
        bool readersFinished = Task.WaitAll(readers, BailOutTimeout);
        // Leave the last bundle alive if a reader is stuck; disposing under it would only mask the hang.
        if (readersFinished) current.Bundle.Dispose();

        TestContext.Out.WriteLine(
            $"commits={commits} disposals={disposals} leasedReads={Volatile.Read(ref leasedReads)} " +
            $"queuedPrewarms={Volatile.Read(ref queuedPrewarms)} foreignReads={Volatile.Read(ref foreignReads)}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readersFinished, Is.True, "reader tasks did not finish");
            Assert.That(readerException, Is.Null);
            Assert.That(Volatile.Read(ref foreignReads), Is.Zero);
            // Prove both churn paths and the leased reads actually ran, so the assertions above are not vacuous.
            Assert.That(commits, Is.GreaterThan(0));
            Assert.That(disposals, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref leasedReads), Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref queuedPrewarms), Is.GreaterThan(0));
        }
    }
}
