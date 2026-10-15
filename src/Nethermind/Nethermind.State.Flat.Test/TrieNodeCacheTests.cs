// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class TrieNodeCacheTests
{
    private static readonly TreePath NodePath = TreePath.FromHexString("abcd");
    private static readonly TreePath OtherNodePath = TreePath.FromHexString("1234");
    private static readonly byte[] RlpA = Bytes.FromHexString(FlatTestHelpers.LeafRlpA);
    private static readonly byte[] RlpB = Bytes.FromHexString(FlatTestHelpers.LeafRlpB);
    private static readonly byte[] RlpC = Bytes.FromHexString(FlatTestHelpers.LeafRlpC);

    private static TrieNodeCache NewCache(ulong memoryBudget) => new(new FlatDbConfig { TrieCacheMemoryBudget = memoryBudget });

    private static TransientResource NewTransientResource(Hash256? address, params (TreePath Path, byte[] Rlp)[] warmedNodes)
    {
        TransientResource transientResource = new ResourcePool(new FlatDbConfig()).GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        foreach ((TreePath path, byte[] rlp) in warmedNodes)
        {
            Assert.That(transientResource.WarmedNodes.TryInsert(address, path, ValueKeccak.Compute(rlp), rlp, out _), Is.True);
        }

        return transientResource;
    }

    /// <summary>Promotes <paramref name="warmedNodes"/> as the nodes warmed by one retired block.</summary>
    private static void Promote(TrieNodeCache cache, Hash256? address, params (TreePath Path, byte[] Rlp)[] warmedNodes)
    {
        TransientResource transientResource = NewTransientResource(address, warmedNodes);
        try
        {
            cache.Add(transientResource);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    private static bool TryGet(TrieNodeCache cache, Hash256? address, in TreePath path, byte[] rlp, out NodeRecordRef record) =>
        cache.TryGet(address, path, ValueKeccak.Compute(rlp), out record);

    private static bool Contains(TrieNodeCache cache, Hash256? address, in TreePath path, byte[] rlp) => TryGet(cache, address, path, rlp, out _);

    [Test]
    public void Add_then_get_checks_the_hash_and_a_collision_replaces([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakA : null;
        Hash256? otherTrieAddress = storage ? null : TestItem.KeccakA;
        TrieNodeCache cache = NewCache(MemorySizes.MiB);

        Promote(cache, address, (NodePath, RlpA), (OtherNodePath, RlpC));
        bool foundA = TryGet(cache, address, NodePath, RlpA, out NodeRecordRef recordA);
        bool foundWithWrongHash = Contains(cache, address, NodePath, RlpB);
        bool foundInOtherTrie = Contains(cache, otherTrieAddress, NodePath, RlpA);
        Promote(cache, address, (NodePath, RlpB));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundA, Is.True);
            Assert.That(recordA.Rlp.ToArray(), Is.EqualTo(RlpA));
            Assert.That(foundWithWrongHash, Is.False);
            Assert.That(foundInOtherTrie, Is.False);
            Assert.That(Contains(cache, address, NodePath, RlpB), Is.True, "colliding node added");
            Assert.That(Contains(cache, address, NodePath, RlpA), Is.False, "collided node replaced");
            Assert.That(Contains(cache, address, OtherNodePath, RlpC), Is.True);
        }
    }

    // A shard is replaced rather than reset, so a reader holding a record of the old shard keeps reading the same bytes.
    [Test]
    public void A_full_shard_is_replaced_and_a_record_held_across_it_stays_readable([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakA : null;
        // Each shard's arena fits one record of these leaves but not two.
        TrieNodeCache cache = NewCache(256 * 64);

        Promote(cache, address, (NodePath, RlpA));
        Assert.That(TryGet(cache, address, NodePath, RlpA, out NodeRecordRef held), Is.True);
        Promote(cache, address, (NodePath, RlpB));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contains(cache, address, NodePath, RlpB), Is.True, "added to the replacement shard");
            Assert.That(Contains(cache, address, NodePath, RlpA), Is.False);
            Assert.That(held.Rlp.ToArray(), Is.EqualTo(RlpA));
            Assert.That(held.Hash, Is.EqualTo(ValueKeccak.Compute(RlpA)));
        }
    }

    // A shard is picked by the first path byte plus the first address byte, so nodes agreeing on that sum share a shard's
    // replacement while any other node keeps its own shard.
    [TestCase(null, "abcd", null, "ab12", true)]
    [TestCase(null, "abcd", null, "ac12", false)]
    [TestCase("01", "10cd", "10", "0112", true)]
    [TestCase("01", "abcd", null, "abcd", false)]
    [TestCase("01", "abcd", "02", "abcd", false)]
    public void Nodes_of_one_shard_share_its_replacement(string? addressByte, string pathHex, string? otherAddressByte, string otherPathHex, bool sameShard)
    {
        Hash256? address = Address(addressByte);
        Hash256? otherAddress = Address(otherAddressByte);
        TreePath path = TreePath.FromHexString(pathHex);
        TreePath otherPath = TreePath.FromHexString(otherPathHex);
        // Each shard's arena fits one record of these leaves but not two.
        TrieNodeCache cache = NewCache(256 * 64);

        Promote(cache, address, (path, RlpA));
        Promote(cache, otherAddress, (otherPath, RlpB));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contains(cache, otherAddress, otherPath, RlpB), Is.True);
            Assert.That(Contains(cache, address, path, RlpA), Is.EqualTo(!sameShard), "replaced with its shard");
        }

        static Hash256? Address(string? firstByte) => firstByte is null ? null : new Hash256(Bytes.FromHexString(firstByte.PadRight(64, '0')));
    }

    [Test]
    public void Add_waits_for_the_exclusive_lease_and_caches_only_with_a_budget([Values] bool storage, [Values(0UL, 1UL << 20)] ulong memoryBudget)
    {
        Hash256? address = storage ? TestItem.KeccakA : null;
        TrieNodeCache cache = NewCache(memoryBudget);
        TransientResource transientResource = NewTransientResource(address, (NodePath, RlpA));
        Assert.That(transientResource.TryAcquireLease(), Is.True, "a warm-up still in flight");

        Task add = Task.Run(() => cache.Add(transientResource));
        bool addedDuringWarmUp = add.Wait(TimeSpan.FromMilliseconds(200));
        transientResource.ReleaseLease();
        bool addedAfterWarmUp = add.Wait(TimeSpan.FromSeconds(30));
        transientResource.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addedDuringWarmUp, Is.False);
            Assert.That(addedAfterWarmUp, Is.True);
            Assert.That(Contains(cache, address, NodePath, RlpA), Is.EqualTo(memoryBudget != 0));
        }
    }

    [Test]
    public void Clear_removes_every_record_and_the_cache_stays_usable([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakA : null;
        TrieNodeCache cache = NewCache(MemorySizes.MiB);
        Promote(cache, address, (NodePath, RlpA), (OtherNodePath, RlpC));

        cache.Clear();
        bool foundAfterClear = Contains(cache, address, NodePath, RlpA) || Contains(cache, address, OtherNodePath, RlpC);
        Promote(cache, address, (NodePath, RlpB));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundAfterClear, Is.False);
            Assert.That(Contains(cache, address, NodePath, RlpB), Is.True);
        }
    }
}
