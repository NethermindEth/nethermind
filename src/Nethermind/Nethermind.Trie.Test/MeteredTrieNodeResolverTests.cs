// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MeteredTrieNodeResolverTests
{
    [Test]
    public void FindCachedOrUnknown_increments_node_lookups()
    {
        ITrieNodeResolver inner = Substitute.For<ITrieNodeResolver>();
        TrieNode dummy = new(NodeType.Unknown, TestItem.KeccakA);
        inner.FindCachedOrUnknown(default, default!).ReturnsForAnyArgs(dummy);

        VisitingStats diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        TreePath path = TreePath.Empty;
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        TreePath deeper = TreePath.FromNibble(stackalloc byte[] { 1, 2, 3, 4, 5 });
        resolver.FindCachedOrUnknown(deeper, TestItem.KeccakB);

        Assert.That(diag.NodeLookups, Is.EqualTo(2));
    }

    [Test]
    public void Accept_observes_depth_for_cached_and_inlined_nodes()
    {
        MemoryNodeStorage storage = new();
        using RawTrieStore trieStore = new(storage);
        PatriciaTree tree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
        tree.Set([0], [1]);
        tree.Set([1], [2]);
        tree.UpdateRootHash();

        TrackingVisitor visitor = new();

        VisitingStats diagnostics = new();
        VisitingOptions visitingOptions = new() { MaxDegreeOfParallelism = 1 };
        tree.Accept(visitor, tree.RootHash, visitingOptions, diagnostics: diagnostics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(visitor.TreesVisited, Is.EqualTo(1));
            Assert.That(visitor.LeavesVisited, Is.EqualTo(2));
            Assert.That(diagnostics.NodeLookups, Is.EqualTo(0));
            Assert.That(diagnostics.MaxDepth, Is.GreaterThan(0));
        }
    }

    [Test]
    public void LoadRlp_increments_cache_misses_so_cache_hits_are_lookups_minus_loads()
    {
        ITrieNodeResolver inner = Substitute.For<ITrieNodeResolver>();
        TrieNode dummy = new(NodeType.Unknown, TestItem.KeccakA);
        inner.FindCachedOrUnknown(default, default!).ReturnsForAnyArgs(dummy);
        inner.LoadRlp(default, default!).ReturnsForAnyArgs(new byte[] { 0xc0 });

        VisitingStats diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        TreePath path = TreePath.Empty;
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        resolver.LoadRlp(path, TestItem.KeccakA);

        Assert.That(diag.NodeLookups, Is.EqualTo(2));
        Assert.That(diag.CacheMisses, Is.EqualTo(1));
        Assert.That(diag.CacheHits, Is.EqualTo(1));
    }

    [Test]
    public void GetStorageTrieNodeResolver_returns_metered_wrapper_sharing_same_diagnostics()
    {
        ITrieNodeResolver inner = Substitute.For<ITrieNodeResolver>();
        ITrieNodeResolver storageInner = Substitute.For<ITrieNodeResolver>();
        TrieNode dummy = new(NodeType.Unknown, TestItem.KeccakA);
        inner.GetStorageTrieNodeResolver(Arg.Any<Hash256?>()).Returns(storageInner);
        storageInner.FindCachedOrUnknown(default, default!).ReturnsForAnyArgs(dummy);

        VisitingStats diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        ITrieNodeResolver storageResolver = resolver.GetStorageTrieNodeResolver(Keccak.Zero);
        Assert.That(storageResolver, Is.TypeOf<MeteredTrieNodeResolver>());
        storageResolver.FindCachedOrUnknown(TreePath.Empty, TestItem.KeccakA);

        Assert.That(diag.NodeLookups, Is.EqualTo(1), "the wrapped storage resolver shares the original diagnostics object");
    }

    private sealed class TrackingVisitor : ITreeVisitor<EmptyContext>
    {
        public bool IsFullDbScan => false;
        public bool ExpectAccounts => false;
        public int TreesVisited { get; private set; }
        public int LeavesVisited { get; private set; }

        public bool ShouldVisit(in EmptyContext nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in EmptyContext nodeContext, in ValueHash256 rootHash) => TreesVisited++;

        public void VisitMissingNode(in EmptyContext nodeContext, in ValueHash256 nodeHash)
        {
        }

        public void VisitBranch(in EmptyContext nodeContext, TrieNode node)
        {
        }

        public void VisitExtension(in EmptyContext nodeContext, TrieNode node)
        {
        }

        public void VisitLeaf(in EmptyContext nodeContext, TrieNode node) => LeavesVisited++;

        public void VisitAccount(in EmptyContext nodeContext, TrieNode node, in AccountStruct account)
        {
        }
    }
}
