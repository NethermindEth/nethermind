// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class VisitingTests
{
    [TestCaseSource(nameof(GetOptions))]
    public void Visitors(VisitingOptions options)
    {
        MemDb memDb = new();

        using TrieStore trieStore = new(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, LimboLogs.Instance);
        PatriciaTree patriciaTree = new(trieStore, LimboLogs.Instance);

        Span<byte> raw = stackalloc byte[32];

        for (int i = 0; i < 64; i++)
        {
            raw.Clear();

            raw[i / 2] = (byte)(1 << (4 * (1 - i % 2)));
            patriciaTree.Set(raw, [Rlp.NullObjectByte]);
        }

        patriciaTree.Commit(0);

        var visitor = new AppendingVisitor();

        patriciaTree.Accept(visitor, patriciaTree.RootHash, options);
    }

    public static IEnumerable<TestCaseData> GetOptions()
    {
        yield return new TestCaseData(new VisitingOptions
        {
            ExpectAccounts = false
        }).SetName("Default");

        yield return new TestCaseData(new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            FullScanMemoryBudget = 1.MiB(),
            ExpectAccounts = false
        }).SetName("Parallel");
    }

    public class AppendingVisitor : ITreeVisitor<AppendingVisitor.PathGatheringContext>
    {
        public IEnumerable<byte[]> LeafPaths => _paths;

        private readonly ConcurrentQueue<byte[]> _paths = new();

        public struct PathGatheringContext(byte[]? nibbles) : INodeContext<PathGatheringContext>
        {
            public byte[] Nibbles => nibbles ?? Array.Empty<byte>();

            public PathGatheringContext Add(byte[] nibblePath)
            {
                var @new = new byte[Nibbles.Length + nibblePath.Length];
                Nibbles.CopyTo(@new, 0);
                nibblePath.CopyTo(@new, Nibbles.Length);

                return new PathGatheringContext(@new);
            }

            public PathGatheringContext Add(byte nibble)
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

        public void VisitExtension(in PathGatheringContext nodeContext, TrieNode node,
            TrieVisitContext trieVisitContext)
        {
        }

        public void VisitLeaf(in PathGatheringContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext,
            byte[] value = null)
        {
            PathGatheringContext context = nodeContext.Add(node.Key!);

            var nibbles = context.Nibbles;

            nibbles.Length.Should().Be(64);
            _paths.Enqueue(nibbles);
        }

        public void VisitCode(in PathGatheringContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
        }
    }
}
