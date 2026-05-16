// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
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

    // -------- Address-salted shard tests --------
    // GetNodeShardIdx exposes the production formula:
    //   hash-mode:       hash.GetHashCode()                          & (count-1)
    //   path-tracked:    prefix                                      & (count-1)   if address is null
    //   path-tracked:    (prefix ^ address.GetHashCode64())          & (count-1)   otherwise
    //
    // Past-key tracking REQUIRES that (address, path) maps to the same shard regardless of
    // the node hash, so the same logical node location can be re-found across hash changes.

    private static TrieStore BuildStore(int shardBit, bool trackPastKeys)
    {
        TestFinalizedStateProvider finalized = new(depth: 64);
        TrieStore store = new(
            new NodeStorage(new MemDb(), requirePath: true),
            Prune.WhenCacheReaches(1.MiB),
            Persist.EveryBlock,
            finalized,
            new PruningConfig
            {
                Mode = PruningMode.Full,
                DirtyNodeShardBit = shardBit,
                TrackPastKeys = trackPastKeys,
            },
            LimboLogs.Instance);
        finalized.TrieStore = store;
        return store;
    }

    [Test]
    public void Past_key_tracking_same_address_and_path_with_different_hashes_pick_same_shard()
    {
        using TrieStore store = BuildStore(shardBit: 8, trackPastKeys: true);
        Hash256 address = TestItem.KeccakA;
        TreePath path = TreePath.FromHexString("ab");

        int shardForHash1 = store.GetNodeShardIdx(address, in path, in TestItem.KeccakB.ValueHash256);
        int shardForHash2 = store.GetNodeShardIdx(address, in path, in TestItem.KeccakC.ValueHash256);
        int shardForHash3 = store.GetNodeShardIdx(address, in path, in TestItem.KeccakD.ValueHash256);

        shardForHash1.Should().Be(shardForHash2,
            "the same (address, path) MUST select the same shard regardless of node hash - past-key tracking invariant");
        shardForHash2.Should().Be(shardForHash3);
    }

    [Test]
    public void Past_key_tracking_state_trie_null_address_falls_back_to_pure_prefix()
    {
        using TrieStore store = BuildStore(shardBit: 8, trackPastKeys: true);
        TreePath path = TreePath.FromHexString("ab");

        int shard = store.GetNodeShardIdx(address: null, in path, in TestItem.KeccakA.ValueHash256);

        // State trie path: no salt, no hash mixing - pure prefix.
        shard.Should().Be(TrieStore.GetPathPrefixShardIdx(in path, shardBit: 8));
        shard.Should().Be(0xab);
    }

    [Test]
    public void Past_key_tracking_same_path_different_addresses_spread_across_shards()
    {
        // Property test: across many random addresses (all sharing the same path),
        // the salt must spread shard residence broadly. Exact uniformity is not required
        // (InstanceRandom-keyed FastHash64For32Bytes is good but not perfect on small samples),
        // but the implementation MUST NOT collapse all addresses to a single shard.
        using TrieStore store = BuildStore(shardBit: 8, trackPastKeys: true);
        TreePath path = TreePath.FromHexString("ab");
        ValueHash256 nodeHash = TestItem.KeccakA.ValueHash256;

        HashSet<int> shardsUsed = [];
        for (int i = 1; i <= 256; i++)
        {
            Hash256 address = Keccak.Compute(i.ToString());
            shardsUsed.Add(store.GetNodeShardIdx(address, in path, in nodeHash));
        }

        // With 256 random addresses over 256 shards, expect well over half to be touched.
        shardsUsed.Count.Should().BeGreaterThan(128,
            "process-lifetime salt + path-prefix XOR must distribute distinct addresses across most shards, not collapse them");
    }

    [Test]
    public void Hash_mode_without_past_key_tracking_ignores_address()
    {
        using TrieStore store = BuildStore(shardBit: 8, trackPastKeys: false);
        TreePath path = TreePath.FromHexString("ab");
        ValueHash256 nodeHash = TestItem.KeccakA.ValueHash256;

        int withAddress = store.GetNodeShardIdx(TestItem.KeccakA, in path, in nodeHash);
        int withoutAddress = store.GetNodeShardIdx(address: null, in path, in nodeHash);

        // Without past-key tracking, the address branch is unreachable; only hash drives the shard.
        withAddress.Should().Be(withoutAddress, "hash-mode shard selection must not vary by address");
        withAddress.Should().Be((int)((uint)nodeHash.GetHashCode() & 255));
    }

    [Test]
    public void Hash_mode_without_past_key_tracking_varies_with_hash()
    {
        using TrieStore store = BuildStore(shardBit: 8, trackPastKeys: false);
        TreePath path = TreePath.FromHexString("ab");

        int shard1 = store.GetNodeShardIdx(null, in path, in TestItem.KeccakA.ValueHash256);
        int shard2 = store.GetNodeShardIdx(null, in path, in TestItem.KeccakB.ValueHash256);

        // Hash-mode: hash IS the discriminator, regardless of path.
        // Two well-distributed keccaks almost certainly land in different shards out of 256.
        shard1.Should().NotBe(shard2);
    }
}
