// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
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

        ProofDiagnostics diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        TreePath path = TreePath.Empty;
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        TreePath deeper = TreePath.FromNibble(stackalloc byte[] { 1, 2, 3, 4, 5 });
        resolver.FindCachedOrUnknown(deeper, TestItem.KeccakB);

        diag.NodeLookups.Should().Be(2);
        diag.MaxDepth.Should().Be(5);
    }

    [Test]
    public void LoadRlp_increments_cache_misses_so_cache_hits_are_lookups_minus_loads()
    {
        ITrieNodeResolver inner = Substitute.For<ITrieNodeResolver>();
        TrieNode dummy = new(NodeType.Unknown, TestItem.KeccakA);
        inner.FindCachedOrUnknown(default, default!).ReturnsForAnyArgs(dummy);
        inner.LoadRlp(default, default!).ReturnsForAnyArgs(new byte[] { 0xc0 });

        ProofDiagnostics diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        TreePath path = TreePath.Empty;
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        resolver.FindCachedOrUnknown(path, TestItem.KeccakA);
        resolver.LoadRlp(path, TestItem.KeccakA);

        diag.NodeLookups.Should().Be(2);
        diag.CacheMisses.Should().Be(1);
        diag.CacheHits.Should().Be(1);
    }

    [Test]
    public void GetStorageTrieNodeResolver_returns_metered_wrapper_sharing_same_diagnostics()
    {
        ITrieNodeResolver inner = Substitute.For<ITrieNodeResolver>();
        ITrieNodeResolver storageInner = Substitute.For<ITrieNodeResolver>();
        TrieNode dummy = new(NodeType.Unknown, TestItem.KeccakA);
        inner.GetStorageTrieNodeResolver(Arg.Any<Hash256?>()).Returns(storageInner);
        storageInner.FindCachedOrUnknown(default, default!).ReturnsForAnyArgs(dummy);

        ProofDiagnostics diag = new();
        MeteredTrieNodeResolver resolver = new(inner, diag);

        ITrieNodeResolver storageResolver = resolver.GetStorageTrieNodeResolver(Keccak.Zero);
        storageResolver.Should().BeOfType<MeteredTrieNodeResolver>();
        storageResolver.FindCachedOrUnknown(TreePath.Empty, TestItem.KeccakA);

        diag.NodeLookups.Should().Be(1, "the wrapped storage resolver shares the original diagnostics object");
    }
}
