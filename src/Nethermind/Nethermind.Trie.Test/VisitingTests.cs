// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
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
    public void Visitors_state(VisitingOptions options, INodeStorage.KeyScheme scheme)
    {
        MemDb memDb = new();

        using ITrieStore trieStore = TestTrieStoreFactory.Build(new NodeStorage(memDb, scheme), LimboLogs.Instance);
        PatriciaTree patriciaTree = new(trieStore, LimboLogs.Instance);

        Span<byte> raw = stackalloc byte[32];

        for (int i = 0; i < 64; i++)
        {
            raw.Clear();

            raw[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            patriciaTree.Set(raw, Rlp.Encode(new Account(10UL, (ulong)(10_000_000 + i))));
        }

        using (trieStore.BeginBlockCommit(0)) { patriciaTree.Commit(); }

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
    public void Visitors_storage(VisitingOptions options, INodeStorage.KeyScheme scheme)
    {
        MemDb memDb = new();

        using ITrieStore trieStore = TestTrieStoreFactory.Build(new NodeStorage(memDb, scheme), LimboLogs.Instance);

        byte[] value = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        Hash256 stateRootHash = Keccak.Zero;

        IBlockCommitter blockCommit = trieStore.BeginBlockCommit(0);

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

        for (int i = 0; i < 64; i++)
        {
            ValueHash256 stateKey = default;
            stateKey.BytesAsSpan[i / 2] = (byte)(1 << (4 * (1 - i % 2)));

            stateTree.Set(stateKey,
                new Account(10UL, (ulong)(10_000_000 + i), stateRootHash, Keccak.OfAnEmptySequenceRlp));
        }

        stateTree.Commit();
        blockCommit.Dispose();

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
    public void Batched_hash_visit_reads_nodes_in_batches_with_same_stats([Values] bool withMissingNode)
    {
        MemDb source = new();
        NodeStorage sourceStorage = new(source, INodeStorage.KeyScheme.Hash);
        StateTree state = new(new RawScopedTrieStore(sourceStorage), LimboLogs.Instance);
        for (int i = 0; i < 64; i++)
        {
            Hash256 addressHash = Keccak.Compute(BitConverter.GetBytes(i));
            PatriciaTree storage = new(new RawScopedTrieStore(sourceStorage, addressHash), LimboLogs.Instance);
            for (int j = 0; j < 4; j++)
            {
                storage.Set(Keccak.Compute(BitConverter.GetBytes(i * 4 + j)).Bytes, [(byte)(j + 1)]);
            }

            storage.Commit();
            state.Set(addressHash, new Account((ulong)i, (ulong)i, storage.RootHash, Keccak.OfAnEmptyString));
        }

        state.Commit();
        Hash256 root = state.RootHash;

        if (withMissingNode)
        {
            source.Remove(source.GetAllKeys().First(key => !key.AsSpan().SequenceEqual(root.Bytes)));
        }

        BatchingMemDb batchingDb = new();
        MemDb plainDb = new();
        foreach (KeyValuePair<byte[], byte[]?> entry in source.GetAll())
        {
            batchingDb[entry.Key] = entry.Value;
            plainDb[entry.Key] = entry.Value;
        }

        TrieStats batched = CollectHashScanStats(batchingDb, root);
        TrieStats single = CollectHashScanStats(plainDb, root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batched.ToString(), Is.EqualTo(single.ToString()));
            Assert.That(batched.MissingNodes, Is.EqualTo(withMissingNode ? 1 : 0));
            Assert.That(batchingDb.BatchReads, Is.GreaterThan(0));
        }
    }

    private static TrieStats CollectHashScanStats(IKeyValueStoreWithBatching db, Hash256 root)
    {
        TestFinalizedStateProvider finalizedStateProvider = new(64);
        using TrieStore trieStore = new(new NodeStorage(db, INodeStorage.KeyScheme.Hash), No.Pruning, Persist.EveryBlock,
            finalizedStateProvider, new PruningConfig(), LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        StateTree tree = new(trieStore.AsReadOnly().GetTrieStore(null), LimboLogs.Instance);
        TrieStatsCollector collector = new(new MemDb(), LimboLogs.Instance);
        tree.Accept(new NodeRecordVisitorAdapter<TrieStatsCollector.Context>(collector), root,
            new VisitingOptions { MaxDegreeOfParallelism = 1, FullScanMemoryBudget = 1.MiB });
        collector.Finish();
        return collector.Stats;
    }

    private static IEnumerable<TestCaseData> GetOptions()
    {
        yield return new TestCaseData(new VisitingOptions
        {
        }, INodeStorage.KeyScheme.HalfPath).SetName("Default");

        yield return new TestCaseData(new VisitingOptions
        {
        }, INodeStorage.KeyScheme.Hash).SetName("Default Hash");

        yield return new TestCaseData(new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            FullScanMemoryBudget = 1.MiB,
        }, INodeStorage.KeyScheme.HalfPath).SetName("Parallel");

        yield return new TestCaseData(new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            FullScanMemoryBudget = 1.MiB,
        }, INodeStorage.KeyScheme.Hash).SetName("Parallel Hash");
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
