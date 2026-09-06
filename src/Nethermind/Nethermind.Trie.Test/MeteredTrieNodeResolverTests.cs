// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MeteredTrieNodeResolverTests
{
    [Test]
    public void FindCachedOrUnknown_increments_node_lookups_and_observes_depth()
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
        Assert.That(diag.MaxDepth, Is.EqualTo(5));
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
}
