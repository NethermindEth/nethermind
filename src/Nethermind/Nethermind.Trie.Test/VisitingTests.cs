// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class VisitingTests
{
    [TestCaseSource(nameof(GetAccountOptions))]
    public void Visitors_state(VisitingOptions options)
    {
        MemDb memDb = new();

        using TrieStore trieStore = new(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, LimboLogs.Instance);
        PatriciaTree patriciaTree = new(trieStore, LimboLogs.Instance);

        Span<byte> raw = stackalloc byte[32];

        for (int i = 0; i < 64; i++)
        {
            raw.Clear();

            raw[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            patriciaTree.Set(raw, Rlp.Encode(new Account(10, (UInt256)(10_000_000 + i))));
        }

        patriciaTree.Commit(0);

        var visitor = new AppendingVisitor();

        patriciaTree.Accept(visitor, patriciaTree.RootHash, options);

        var setNibbles = new HashSet<int>(Enumerable.Range(0, 64));

        foreach (var path in visitor.LeafPaths)
        {
            path.Length.Should().Be(64);

            var index = path.AsSpan().IndexOfAnyExcept((byte)0);

            path.AsSpan(index + 1).IndexOfAnyExcept((byte)0).Should()
                .Be(-1, "Shall not found other values than the one nibble set");
            path[index].Should().Be(1, "The given set should be 1 as this is the only nibble");
            setNibbles.Remove(index).Should().BeTrue("The nibble should not have been removed before");
        }
    }

    [TestCaseSource(nameof(GetStorageOptions))]
    public void Visitors_storage(VisitingOptions options)
    {
        MemDb memDb = new();

        using TrieStore trieStore = new(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, LimboLogs.Instance);
        StorageTree storage = new(trieStore, LimboLogs.Instance);

        byte[] value = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        for (int i = 0; i < 64; i++)
        {
            ValueHash256 storageKey = default;
            storageKey.BytesAsSpan[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            storage.Set(storageKey, value);
        }

        storage.Commit(0);

        StateTree stateTree = new(trieStore, LimboLogs.Instance);

        for (int i = 0; i < 64; i++)
        {
            ValueHash256 stateKey = default;
            stateKey.BytesAsSpan[i / 2] = (byte)(1 << (4 * (1 - i % 2)));

            stateTree.Set(stateKey,
                new Account(10, (UInt256)(10_000_000 + i), storage.RootHash, Keccak.OfAnEmptySequenceRlp));
        }

        stateTree.Commit(0);

        var visitor = new AppendingVisitor();

        stateTree.Accept(visitor, stateTree.RootHash, options);

        foreach (var path in visitor.LeafPaths)
        {
            if (path.Length == 64)
            {
                AssertPath(path);
            }
            else
            {
                path.Length.Should().Be(128);

                var accountPart = path.Slice(0, 64);
                var storagePart = path.Slice(64);

                AssertPath(accountPart);
                AssertPath(storagePart);
            }
        }

        return;

        static void AssertPath(ReadOnlySpan<byte> path)
        {
            var index = path.IndexOfAnyExcept((byte)0);
            path.Slice(index + 1).IndexOfAnyExcept((byte)0).Should()
                .Be(-1, "Shall not found other values than the one nibble set");
            path[index].Should().Be(1, "The given set should be 1 as this is the only nibble");
        }
    }

    public static IEnumerable<TestCaseData> GetAccountOptions() => GetOptions(false);
    public static IEnumerable<TestCaseData> GetStorageOptions() => GetOptions(true);

    private static IEnumerable<TestCaseData> GetOptions(bool expectAccounts)
    {
        yield return new TestCaseData(new VisitingOptions
        {
            ExpectAccounts = expectAccounts
        }).SetName("Default");

        yield return new TestCaseData(new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            FullScanMemoryBudget = 1.MiB(),
            ExpectAccounts = expectAccounts
        }).SetName("Parallel");
    }

    public class AppendingVisitor : ITreeVisitor<AppendingVisitor.PathGatheringContext>
    {
        public IEnumerable<byte[]> LeafPaths => _paths;

        private readonly ConcurrentQueue<byte[]> _paths = new();

        public readonly struct PathGatheringContext(byte[]? nibbles) : INodeContext<PathGatheringContext>
        {
            public readonly byte[] Nibbles => nibbles ?? Array.Empty<byte>();

            public readonly PathGatheringContext Add(ReadOnlySpan<byte> nibblePath)
            {
                var @new = new byte[Nibbles.Length + nibblePath.Length];
                Nibbles.CopyTo(@new, 0);
                nibblePath.CopyTo(@new.AsSpan(Nibbles.Length));

                return new PathGatheringContext(@new);
            }

            public readonly PathGatheringContext Add(byte nibble)
            {
                var @new = new byte[Nibbles.Length + 1];
                Nibbles.CopyTo(@new, 0);
                @new[Nibbles.Length] = nibble;

                return new PathGatheringContext(@new);
            }
        }

        public bool IsFullDbScan => true;

        public bool ShouldVisit(in PathGatheringContext nodeContext, Hash256 nextNode) => true;

        public void VisitTree(in PathGatheringContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(in PathGatheringContext nodeContext, Hash256 nodeHash,
            TrieVisitContext trieVisitContext)
        {
            throw new System.Exception("Should not happen");
        }

        public void VisitBranch(in PathGatheringContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitExtension(in PathGatheringContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitLeaf(in PathGatheringContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            PathGatheringContext context = nodeContext.Add(node.Key!);
            _paths.Enqueue(context.Nibbles);
        }

        public void VisitCode(in PathGatheringContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
        }
    }
}
