// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig());

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

    private SnapshotBundle NewBundle(Action<SnapshotContent>? persisted = null) =>
        new(FlatTestHelpers.MakeBundle(_pool, persisted), new NullTrieNodeCache(), _pool, ResourcePool.Usage.MainBlockProcessing);

    // The trie warmer warms nodes from persistence only, so it must never read the recyclable _snapshots /
    // _transientResource: those are the writes a concurrent block scope recycles under it. A node that only
    // exists as a pending in-memory write is visible to a normal read but Unknown to the warmer; a node in
    // persistence is returned by both.
    [Test]
    public void Trie_warmer_reads_persistence_only_and_ignores_in_memory_writes()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");
        TrieNode persistedNode = new(NodeType.Leaf, new byte[] { 0xC1, 0x80 });
        Hash256 storageAddress = TestItem.KeccakC;
        TreePath persistedStoragePath = TreePath.FromHexString("cd");
        TrieNode persistedStorageNode = new(NodeType.Leaf, new byte[] { 0xC1, 0x81 });

        using SnapshotBundle bundle = NewBundle(content =>
        {
            content.StateNodes[persistedPath] = persistedNode;
            content.StorageNodes[(storageAddress, persistedStoragePath)] = persistedStorageNode;
        });

        TreePath inMemoryPath = TreePath.FromHexString("12");
        TrieNode inMemoryNode = new(NodeType.Unknown, TestItem.KeccakA);
        bundle.SetStateNode(inMemoryPath, inMemoryNode);

        using (Assert.EnterMultipleScope())
        {
            // Normal reads see the pending in-memory write; the warmer does not.
            Assert.That(bundle.FindStateNodeOrUnknown(inMemoryPath, TestItem.KeccakA), Is.SameAs(inMemoryNode));
            Assert.That(bundle.FindStateNodeOrUnknownForTrieWarmer(inMemoryPath, TestItem.KeccakA).NodeType, Is.EqualTo(NodeType.Unknown));

            // The warmer still returns nodes that are in persistence (state and storage).
            Assert.That(bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakB), Is.SameAs(persistedNode));
            Assert.That(bundle.FindStorageNodeOrUnknownTrieWarmer(storageAddress, persistedStoragePath, TestItem.KeccakB), Is.SameAs(persistedStorageNode));
        }
    }

    // Regression for the warmer recycle-under-reader race: warmer-shaped readers hold only a
    // ReadOnlySnapshotBundle lease (as the real warmer job does) while the owner churns and disposes bundles.
    // Because the warmer reads persistence only, it must never crash or observe a foreign node here. A version
    // that read the recyclable _snapshots / _transientResource could return a torn or foreign value under churn.
    [Test]
    public void Trie_warmer_reads_survive_owner_churn_without_foreign_values()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");
        TrieNode persistedNode = new(NodeType.Leaf, new byte[] { 0xC1, 0x80 });

        SnapshotBundle NewChurnBundle() => NewBundle(content => content.StateNodes[persistedPath] = persistedNode);

        long leasedReads = 0;
        long anomalies = 0;
        Exception? readerException = null;
        bool stop = false;
        SnapshotBundle published = NewChurnBundle();

        Task[] readers = new Task[Math.Max(2, Environment.ProcessorCount - 2)];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        SnapshotBundle bundle = Volatile.Read(ref published);
                        if (!bundle.TryLeaseReadOnlyBundle()) continue;
                        try
                        {
                            TrieNode node = bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakA);
                            if (!ReferenceEquals(node, persistedNode)) Interlocked.Increment(ref anomalies);
                            Interlocked.Increment(ref leasedReads);
                        }
                        finally
                        {
                            bundle.ReleaseReadOnlyBundleLease();
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
        SnapshotBundle previous = published;
        while (sw.ElapsedMilliseconds < 2_000 && !Volatile.Read(ref stop))
        {
            SnapshotBundle next = NewChurnBundle();
            Volatile.Write(ref published, next);
            previous.Dispose();
            previous = next;
            epochs++;
        }

        Volatile.Write(ref stop, true);
        Task.WaitAll(readers);
        previous.Dispose();

        TestContext.Out.WriteLine($"epochs={epochs} leasedReads={Volatile.Read(ref leasedReads)} anomalies={Volatile.Read(ref anomalies)}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readerException, Is.Null);
            Assert.That(Volatile.Read(ref anomalies), Is.Zero);
            // Prove the churn loop and the leased reads actually ran, so the assertions above are not vacuous.
            Assert.That(epochs, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref leasedReads), Is.GreaterThan(0));
        }
    }
}
