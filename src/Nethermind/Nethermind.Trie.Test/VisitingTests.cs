// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class VisitingTests
{
    [TestCaseSource(nameof(GetOptions))]
    public void Visitors_state(VisitingOptions options)
    {
        MemDb memDb = new();

        using ITrieStore trieStore = new TestRawTrieStore(memDb);
        PatriciaTree patriciaTree = new(trieStore, LimboLogs.Instance);

        Span<byte> raw = stackalloc byte[32];

        for (int i = 0; i < 64; i++)
        {
            raw.Clear();

            raw[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            patriciaTree.Set(raw, Rlp.Encode(new Account(10UL, (ulong)(10_000_000 + i))));
        }

        patriciaTree.Commit();

        AppendingVisitor visitor = new(false);

        patriciaTree.Accept(visitor, patriciaTree.RootHash, options);

        HashSet<int> setNibbles = [.. Enumerable.Range(0, 64)];

        foreach (byte[] path in visitor.LeafPaths)
        {
            Assert.That(path.Length, Is.EqualTo(64));

            int index = path.AsSpan().IndexOfAnyExcept((byte)0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(path.AsSpan(index + 1).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "Shall not found other values than the one nibble set");
                Assert.That(path[index], Is.EqualTo(1), "The given set should be 1 as this is the only nibble");
                Assert.That(setNibbles.Remove(index), Is.True, "The nibble should not have been removed before");
            }
        }
    }

    [TestCaseSource(nameof(GetOptions))]
    public void Visitors_storage(VisitingOptions options)
    {
        MemDb memDb = new();

        using ITrieStore trieStore = new TestRawTrieStore(memDb);

        byte[] value = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        Hash256 stateRootHash = Keccak.Zero;

        for (int outerIndex = 0; outerIndex < 64; outerIndex++)
        {
            ValueHash256 stateKey = default;
            stateKey.BytesAsSpan[outerIndex / 2] = (byte)(1 << (4 * (1 - outerIndex % 2)));

            StorageTree storage = new(trieStore.GetTrieStore(stateKey.ToCommitment()), LimboLogs.Instance);
            for (int i = 0; i < 64; i++)
            {
                ValueHash256 storageKey = default;
                storageKey.BytesAsSpan[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
                storage.Set(storageKey, value);
            }
            storage.Commit();

            stateRootHash = storage.RootHash;
        }


        StateTree stateTree = new(trieStore, LimboLogs.Instance);
        List<Hash256> accountPaths = [];

        for (int i = 0; i < 64; i++)
        {
            ValueHash256 stateKey = default;
            stateKey.BytesAsSpan[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            accountPaths.Add(stateKey.ToCommitment());

            stateTree.Set(stateKey,
                new Account(10UL, (ulong)(10_000_000 + i), stateRootHash, Keccak.OfAnEmptySequenceRlp));
        }

        stateTree.Commit();

        AppendingVisitor visitor = new(true);

        stateTree.Accept(visitor, stateTree.RootHash, options);

        int totalPath = 0;

        foreach (byte[] path in visitor.LeafPaths)
        {
            totalPath++;
            if (path.Length == 64)
            {
                AssertPath(path);
            }
            else
            {
                Assert.That(path.Length, Is.EqualTo(128));

                byte[] accountPart = path.Slice(0, 64);
                byte[] storagePart = path.Slice(64);

                AssertPath(accountPart);
                AssertPath(storagePart);
            }
        }

        Assert.That(totalPath, Is.EqualTo(4160));

        StorageAddressCollector storageAddresses = new();
        stateTree.Accept(storageAddresses, stateTree.RootHash, options);
        Assert.That(storageAddresses.Addresses, Is.EquivalentTo(accountPaths));

        return;

        static void AssertPath(ReadOnlySpan<byte> path)
        {
            int index = path.IndexOfAnyExcept((byte)0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(path[(index + 1)..].IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "Shall not found other values than the one nibble set");
                Assert.That(path[index], Is.EqualTo(1), "The given set should be 1 as this is the only nibble");
            }
        }
    }

    [Test]
    public void Walk_reports_a_missing_node_and_visits_the_rest([Values(1, 4)] int workers)
    {
        using SixteenBranchTrie trie = SixteenBranchTrie.Build(removeChild: true);

        RecordingVisitor recursive = new(throwOnMissingNode: false);
        Assert.That(Walk(trie, recursive, workers: 1), Is.Null, "recursive walk");
        RecordingVisitor parallel = new(throwOnMissingNode: false);
        Exception? thrown = Walk(trie, parallel, workers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(parallel.Missing, Is.EqualTo(new[] { trie.Removed }));
            Assert.That(parallel.Branches, Is.EquivalentTo(trie.ExpectedBranches));
            Assert.That(parallel.Leaves, Is.EquivalentTo(trie.ExpectedLeaves));
            Assert.That(parallel.Branches, Is.EquivalentTo(recursive.Branches), "same branches as the recursive walk");
            Assert.That(parallel.Leaves, Is.EquivalentTo(recursive.Leaves), "same leaves as the recursive walk");
            Assert.That(recursive.Missing, Is.EqualTo(new[] { trie.Removed }));
        }
    }

    [Test]
    public void Walk_rethrows_what_the_visitor_throws_on_a_missing_node([Values(1, 4)] int workers)
    {
        using SixteenBranchTrie trie = SixteenBranchTrie.Build(removeChild: true);

        Exception? thrown = Walk(trie, new RecordingVisitor(throwOnMissingNode: true), workers);

        AssertFailedWithOnly<InvalidOperationException>(thrown);
    }

    [Test]
    public void Walk_rethrows_the_cancellation_of_stats_collection([Values(1, 4)] int workers)
    {
        // The verify trie shape: the stats collector throws on its first branch once cancelled.
        using SixteenBranchTrie trie = SixteenBranchTrie.Build(removeChild: false);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        TrieStatsCollector collector = new(new MemDb(), LimboLogs.Instance, cancelled.Token);

        Exception? thrown = Walk(trie, new NodeRecordVisitorAdapter<TrieStatsCollector.Context>(collector), workers);

        AssertFailedWithOnly<OperationCanceledException>(thrown);
    }

    /// <summary>Elapses only when a walk is stuck: its workers then wait for jobs that nobody will finish.</summary>
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(30);

    private static Exception? Walk<TContext>(SixteenBranchTrie trie, ITreeVisitor<TContext> visitor, int workers)
        where TContext : struct, INodeContext<TContext>
    {
        // A fresh tree, so that Accept cannot start from an in-memory root that still holds the removed node.
        PatriciaTree tree = new(trie.Store, LimboLogs.Instance);
        VisitingOptions options = new() { MaxDegreeOfParallelism = workers };

        Task walk = Task.Run(() => tree.Accept(visitor, trie.RootHash, options));

        using WaitHandle completed = ((IAsyncResult)walk).AsyncWaitHandle;
        Assert.That(completed.WaitOne(WalkTimeout), Is.True, $"The walk did not end within {WalkTimeout}");
        return walk.Exception?.InnerException;
    }

    private static void AssertFailedWithOnly<TException>(Exception? thrown) where TException : Exception
    {
        IEnumerable<Exception> failures = thrown is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : thrown is null ? [] : [thrown];
        Assert.That(failures, Is.Not.Empty.And.All.InstanceOf<TException>());
    }

    /// <summary>
    /// A Hash-keyed trie whose root is a branch of 16 hashed branches, each with a few hashed account leaves; optionally
    /// with the root's child at nibble 8 removed from the state DB.
    /// </summary>
    private sealed record SixteenBranchTrie(MemDb Database, ITrieStore Store, Hash256 RootHash, Hash256 Removed, List<Hash256> ExpectedBranches, List<Hash256> ExpectedLeaves) : IDisposable
    {
        private const int RemovedChild = 8;
        private const int LeavesPerChild = 3;

        public static SixteenBranchTrie Build(bool removeChild)
        {
            MemDb database = new();
            ITrieStore trieStore = new TestRawTrieStore(database);
            PatriciaTree tree = new(trieStore, LimboLogs.Instance);
            byte[] key = new byte[32];
            for (int child = 0; child < 16; child++)
            {
                for (int leaf = 0; leaf < LeavesPerChild; leaf++)
                {
                    key[0] = (byte)(child << 4 | leaf);
                    // Distinct accounts, so that no two leaves share a hash.
                    tree.Set(key, Rlp.Encode(new Account((ulong)child, (ulong)leaf)));
                }
            }

            tree.Commit();

            // Read the shape back from the store rather than from the tree that built it.
            IScopedTrieStore store = trieStore.GetTrieStore(null);
            TrieNode root = Resolve(store, tree.RootHash);
            Hash256 removed = root.GetChildHash(RemovedChild)!;
            List<Hash256> branches = [tree.RootHash];
            List<Hash256> leaves = [];
            for (int child = 0; child < 16; child++)
            {
                Hash256 childHash = root.GetChildHash(child)!;
                if (removeChild && child == RemovedChild) continue;

                branches.Add(childHash);
                TrieNode childNode = Resolve(store, childHash);
                for (int leaf = 0; leaf < LeavesPerChild; leaf++)
                {
                    leaves.Add(childNode.GetChildHash(leaf)!);
                }
            }

            int children = removeChild ? 15 : 16;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(branches, Has.Count.EqualTo(children + 1).And.None.Null, "every child branch is stored by its hash");
                Assert.That(leaves, Has.Count.EqualTo(children * LeavesPerChild).And.None.Null.And.Unique, "every leaf is stored by its hash");
            }

            if (removeChild) database.Remove(removed.Bytes);
            return new SixteenBranchTrie(database, trieStore, tree.RootHash, removed, branches, leaves);
        }

        public void Dispose() => Database.Dispose();

        private static TrieNode Resolve(IScopedTrieStore store, Hash256 hash)
        {
            TrieNode node = store.FindCachedOrUnknown(TreePath.Empty, hash);
            node.ResolveNode(store, TreePath.Empty);
            return node;
        }
    }

    private sealed class RecordingVisitor(bool throwOnMissingNode) : ITreeVisitor<EmptyContext>
    {
        public ConcurrentQueue<Hash256> Branches { get; } = new();
        public ConcurrentQueue<Hash256> Leaves { get; } = new();
        public ConcurrentQueue<Hash256> Missing { get; } = new();

        public bool IsFullDbScan => true;
        public bool ShouldVisit(in EmptyContext nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in EmptyContext nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in EmptyContext nodeContext, in ValueHash256 nodeHash)
        {
            Missing.Enqueue(nodeHash.ToCommitment());
            if (throwOnMissingNode) throw new InvalidOperationException($"Missing node {nodeHash}");
        }

        public void VisitBranch(in EmptyContext nodeContext, TrieNode node) => Branches.Enqueue(node.Keccak!);
        public void VisitExtension(in EmptyContext nodeContext, TrieNode node) { }
        public void VisitLeaf(in EmptyContext nodeContext, TrieNode node) => Leaves.Enqueue(node.Keccak!);
        public void VisitAccount(in EmptyContext nodeContext, TrieNode node, in AccountStruct account) { }
    }

    private static IEnumerable<TestCaseData> GetOptions()
    {
        yield return new TestCaseData(new VisitingOptions
        {
        }).SetName("Default");

        yield return new TestCaseData(new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }).SetName("Parallel");
    }

    public class AppendingVisitor(bool expectAccount) : ITreeVisitor<AppendingVisitor.PathGatheringContext>
    {
        public bool ExpectAccounts => expectAccount;
        public IEnumerable<byte[]> LeafPaths => _paths;

        private readonly ConcurrentQueue<byte[]> _paths = new();

        public readonly struct PathGatheringContext(byte[]? nibbles) : INodeContext<PathGatheringContext>
        {
            public readonly byte[] Nibbles => nibbles ?? [];

            public readonly PathGatheringContext Add(ReadOnlySpan<byte> nibblePath)
            {
                byte[] @new = new byte[Nibbles.Length + nibblePath.Length];
                Nibbles.CopyTo(@new, 0);
                nibblePath.CopyTo(@new.AsSpan(Nibbles.Length));

                return new PathGatheringContext(@new);
            }

            public readonly PathGatheringContext Add(byte nibble)
            {
                byte[] @new = new byte[Nibbles.Length + 1];
                Nibbles.CopyTo(@new, 0);
                @new[Nibbles.Length] = nibble;

                return new PathGatheringContext(@new);
            }

            public PathGatheringContext AddStorage(in ValueHash256 storage) => this;
        }

        public bool IsFullDbScan => true;

        public bool ShouldVisit(in PathGatheringContext nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in PathGatheringContext nodeContext, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in PathGatheringContext nodeContext, in ValueHash256 nodeHash) => throw new System.Exception("Should not happen");

        public void VisitBranch(in PathGatheringContext nodeContext, TrieNode node)
        {
        }

        public void VisitExtension(in PathGatheringContext nodeContext, TrieNode node)
        {
        }

        public void VisitLeaf(in PathGatheringContext nodeContext, TrieNode node)
        {
            PathGatheringContext context = nodeContext.Add(node.Key!);
            _paths.Enqueue(context.Nibbles);
        }

        public void VisitAccount(in PathGatheringContext nodeContext, TrieNode node, in AccountStruct account)
        {
        }
    }
}
