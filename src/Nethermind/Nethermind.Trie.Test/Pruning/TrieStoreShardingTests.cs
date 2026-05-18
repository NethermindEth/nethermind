// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning;

[Parallelizable(ParallelScope.All)]
public class TrieStoreShardingTests
{
    [TestCase("", 4, 0)]
    [TestCase("", 8, 0)]
    [TestCase("", 12, 0)]
    [TestCase("a", 4, 0xa)]
    [TestCase("a", 8, 0xa0)]
    [TestCase("ab", 8, 0xab)]
    [TestCase("abc", 12, 0xabc)]
    // Long path: only the leading shardBit bits should drive the shard.
    [TestCase("abcdef", 4, 0xa)]
    [TestCase("abcdef", 8, 0xab)]
    [TestCase("abcdef", 12, 0xabc)]
    public void Path_prefix_maps_to_expected_shard(string hex, int shardBit, int expected)
    {
        TreePath path = hex.Length == 0 ? TreePath.Empty : TreePath.FromHexString(hex);
        TrieStore.GetPathPrefixShardIdx(in path, shardBit).Should().Be(expected);
    }

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(12)]
    [TestCase(16)]
    public void Shard_index_in_range_for_any_supported_shardBit(int shardBit)
    {
        TreePath path = TreePath.FromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        int shard = TrieStore.GetPathPrefixShardIdx(in path, shardBit);
        shard.Should().BeInRange(0, (1 << shardBit) - 1);
    }
}
