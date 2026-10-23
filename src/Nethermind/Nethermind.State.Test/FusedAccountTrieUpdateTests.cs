// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Bench (B', FlatDb.FusedAccountTrieUpdate): the fused account-trie pass against the write and hash it replaces, which
/// is the reference. Both must reach the same root and commit the same nodes, on a pre-block trie, for every input.
/// </summary>
public class FusedAccountTrieUpdateTests
{
    private const int Seeds = 1000;
    private const int SeedChunks = 10;
    private const int AllNibbles = 0xFFFF;

    // DirtyNodeHasher.MinimumDirtyNodes: a smaller subtree is left to the root's hashing on either path.
    private const int MinimumHashedSubtree = 4;

    [SetUp]
    public void RequireFusablePlatform()
    {
        if (!Avx2.IsSupported || Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("The fused pass needs AVX2 and more than one core.");
    }

    [Test]
    public void Randomized_batches_match_the_two_passes([Values(1, 128)] int minAccounts, [Range(0, SeedChunks - 1)] int chunk)
    {
        const int seedsPerChunk = Seeds / SeedChunks;
        int fused = 0;
        for (int seed = chunk * seedsPerChunk; seed < (chunk + 1) * seedsPerChunk; seed++)
        {
            RandomBlock(seed, out Dictionary<AddressAsKey, Account?> pre, out Dictionary<AddressAsKey, Account?> batch, out string shape);
            bool[] ran = AssertSameAsTwoPasses(pre, [batch], minAccounts, hashBetweenBatches: false,
                $"seed {seed} ({shape}, pre {pre.Count}, batch {batch.Count}, min {minAccounts})");
            Assert.That(ran[0], Is.EqualTo(batch.Count >= Math.Max(minAccounts, 1)), $"seed {seed}: fused");
            if (ran[0]) fused++;
        }

        // Most batches are past either minimum, so the fused pass is what most seeds test.
        Assert.That(fused, Is.GreaterThan(seedsPerChunk / 2));
    }

    public enum Edge
    {
        CreatesOnEmptyTree,
        Updates,
        Deletes,
        WholeNibbleDeleted,
        AllButOneNibbleDeleted,
        AllButOneAccountDeleted,
        NibbleWithOneEntry,
        RootIsLeaf,
        RootIsLeafAndUpdated,
        RootIsExtension,
        RootIsExtensionOneNibbleBatch,
        EmptyResult,
        EmptyBatch,
        AbsentDeletesOnly,
        NoOpRewrites,
        TotallyEmptyAccounts,
    }

    [Test]
    public void Edge_cases_match_the_two_passes([Values] Edge edge, [Values(1, 128)] int minAccounts)
    {
        BuildEdge(edge, out Dictionary<AddressAsKey, Account?> pre, out Dictionary<AddressAsKey, Account?> batch);
        bool fused = AssertSameAsTwoPasses(pre, [batch], minAccounts, hashBetweenBatches: false, $"{edge}, min {minAccounts}")[0];
        Assert.That(fused, Is.EqualTo(batch.Count >= Math.Max(minAccounts, 1)), "fused");
    }

    [Test]
    public void Edge_case_shapes_are_what_they_say()
    {
        BuildEdge(Edge.RootIsLeaf, out Dictionary<AddressAsKey, Account?> leaf, out _);
        BuildEdge(Edge.RootIsExtension, out Dictionary<AddressAsKey, Account?> extension, out Dictionary<AddressAsKey, Account?> allNibbles);
        BuildEdge(Edge.NibbleWithOneEntry, out _, out Dictionary<AddressAsKey, Account?> oneInNine);
        BuildEdge(Edge.AllButOneNibbleDeleted, out Dictionary<AddressAsKey, Account?> pre, out Dictionary<AddressAsKey, Account?> allButOne);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RootOf(leaf).IsLeaf, Is.True, "leaf root");
            Assert.That(RootOf(extension).IsExtension, Is.True, "extension root");
            Assert.That(Nibbles(allNibbles.Keys), Is.EqualTo(AllNibbles), "all sixteen nibbles");
            Assert.That(oneInNine.Keys.Count(key => TopNibble(key.Value) == 9), Is.EqualTo(1), "one entry in nibble 9");
            Dictionary<AddressAsKey, Account?> after = new(pre);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in allButOne)
            {
                if (entry.Value is null) after.Remove(entry.Key);
                else after[entry.Key] = entry.Value;
            }
            Assert.That(RootOf(after).IsExtension, Is.True, "collapsed to an extension");
        }
    }

    [Test]
    public void A_second_batch_on_a_dirty_root_takes_the_two_passes([Values] bool hashBetween, [Values(1, 128)] int minAccounts)
    {
        Random rng = new(11);
        Dictionary<AddressAsKey, Account?> pre = NewAccounts(rng, 2000, AllNibbles);
        List<Address> existing = [.. pre.Keys.Select(static key => key.Value)];
        Dictionary<AddressAsKey, Account?> first = [];
        for (int i = 0; i < 300; i++) first[Pick(rng, existing)] = RandomAccount(rng);
        Dictionary<AddressAsKey, Account?> second = [];
        for (int i = 0; i < 200; i++) second[Pick(rng, existing)] = RandomAccount(rng);
        for (int i = 0; i < 50; i++) second[Pick(rng, existing)] = null;
        foreach (KeyValuePair<AddressAsKey, Account?> created in NewAccounts(rng, 100, AllNibbles)) second[created.Key] = created.Value;

        bool[] fused = AssertSameAsTwoPasses(pre, [first, second], minAccounts, hashBetween, $"two batches, hash between {hashBetween}");
        Assert.That(fused, Is.EqualTo(new[] { true, false }));
    }

    [TestCase(127, false)]
    [TestCase(128, true)]
    public void Below_the_minimum_the_two_passes_run(int count, bool expected)
    {
        Random rng = new(count);
        Dictionary<AddressAsKey, Account?> pre = NewAccounts(rng, 1000, AllNibbles);
        Dictionary<AddressAsKey, Account?> batch = NewAccounts(rng, count, AllNibbles);
        bool fused = AssertSameAsTwoPasses(pre, [batch], 128, hashBetweenBatches: false, $"{count} accounts")[0];
        Assert.That(fused, Is.EqualTo(expected));
    }

    [Test]
    public void A_missing_node_fails_both_passes_alike([Values(1, 128)] int minAccounts, [Values(100, 300)] int batchSize)
    {
        Random rng = new(batchSize);
        Dictionary<AddressAsKey, Account?> pre = NewAccounts(rng, 2000, AllNibbles);
        (MemDb preDb, Hash256 preRoot) = BuildPreBlock(pre);
        // Only the root is left, so every nibble's write fails loading the root's child.
        foreach (byte[] key in preDb.GetAllKeys().ToArray())
        {
            if (Keccak.Compute(preDb[key]!) != preRoot) preDb[key] = null;
        }

        List<Address> existing = [.. pre.Keys.Select(static key => key.Value)];
        Dictionary<AddressAsKey, Account?> batch = [];
        while (batch.Count < batchSize) batch[Pick(rng, existing)] = RandomAccount(rng);

        StateTree off = new(new RawScopedTrieStore(Copy(preDb)), LimboLogs.Instance) { RootHash = preRoot };
        StateTree on = new(new RawScopedTrieStore(Copy(preDb)), LimboLogs.Instance) { RootHash = preRoot };
        TrieNode? rootBefore = on.RootRef;
        Exception? offException = Assert.Catch(() => off.WriteAccounts(batch));
        Exception? onException = Assert.Catch(() => on.WriteAccountsFused(batch, minAccounts));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offException, Is.Not.Null);
            Assert.That(onException, Is.TypeOf(offException!.GetType()));
            Assert.That(on.RootRef, Is.SameAs(rootBefore), "root left as it was");
        }
    }

    /// <summary>Runs every batch through both paths on copies of one pre-block trie and compares roots and committed nodes.</summary>
    /// <returns>Whether each batch took the fused pass.</returns>
    private static bool[] AssertSameAsTwoPasses(Dictionary<AddressAsKey, Account?> pre, IReadOnlyList<Dictionary<AddressAsKey, Account?>> batches,
        int minAccounts, bool hashBetweenBatches, string label)
    {
        (MemDb preDb, Hash256 preRoot) = BuildPreBlock(pre);
        MemDb dbOff = Copy(preDb);
        MemDb dbOn = Copy(preDb);
        StateTree off = new(new RawScopedTrieStore(dbOff), LimboLogs.Instance) { RootHash = preRoot };
        StateTree on = new(new RawScopedTrieStore(dbOn), LimboLogs.Instance) { RootHash = preRoot };

        bool[] fused = new bool[batches.Count];
        for (int i = 0; i < batches.Count; i++)
        {
            off.WriteAccounts(batches[i]);
            fused[i] = on.WriteAccountsFused(batches[i], minAccounts);
            if (fused[i]) AssertBelowRootHashed(on, $"{label}, batch {i}");

            if (hashBetweenBatches || i == batches.Count - 1)
            {
                off.UpdateRootHash();
                on.UpdateRootHash();
                Assert.That(on.RootHash, Is.EqualTo(off.RootHash), $"{label}: root after batch {i}");
            }
        }

        off.Commit();
        on.Commit();
        Assert.That(on.RootHash, Is.EqualTo(off.RootHash), $"{label}: committed root");

        HashSet<string> nodesOff = Contents(dbOff);
        HashSet<string> nodesOn = Contents(dbOn);
        if (!nodesOn.SetEquals(nodesOff))
        {
            Assert.Fail($"{label}: committed nodes differ, {nodesOn.Except(nodesOff).Count()} only fused, {nodesOff.Except(nodesOn).Count()} only two passes");
        }

        return fused;
    }

    /// <summary>The fused pass leaves only the root to hash: every dirty child of the root is hashed, or too small for the level order.</summary>
    private static void AssertBelowRootHashed(PatriciaTree tree, string label)
    {
        TrieNode? root = tree.RootRef;
        if (root is null || !root.IsDirty) return;

        int children = root.IsBranch ? 16 : root.IsExtension ? 1 : 0;
        for (int i = 0; i < children; i++)
        {
            if (!root.TryGetDirtyChild(i, out TrieNode? child) || child.Keccak is not null) continue;
            int unhashed = CountDirtyUnhashed(child);
            if (unhashed >= MinimumHashedSubtree) Assert.Fail($"{label}: child {i} of the root left with {unhashed} unhashed dirty nodes");
        }
    }

    private static int CountDirtyUnhashed(TrieNode node)
    {
        int count = 1;
        int children = node.IsBranch ? 16 : node.IsExtension ? 1 : 0;
        for (int i = 0; i < children; i++)
        {
            if (node.TryGetDirtyChild(i, out TrieNode? child) && child.Keccak is null) count += CountDirtyUnhashed(child);
        }

        return count;
    }

    private static (MemDb Db, Hash256 Root) BuildPreBlock(Dictionary<AddressAsKey, Account?> accounts)
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.WriteAccounts(accounts);
        tree.UpdateRootHash();
        tree.Commit();
        return (db, tree.RootHash);
    }

    private static TrieNode RootOf(Dictionary<AddressAsKey, Account?> accounts)
    {
        (MemDb db, Hash256 root) = BuildPreBlock(accounts);
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance) { RootHash = root };
        TrieNode node = tree.RootRef!;
        node.ResolveNode(tree.TrieStore, TreePath.Empty);
        return node;
    }

    private static MemDb Copy(MemDb db)
    {
        MemDb copy = new();
        foreach (KeyValuePair<byte[], byte[]> entry in db.GetAll()) copy[entry.Key] = entry.Value;
        return copy;
    }

    private static HashSet<string> Contents(MemDb db) =>
        [.. db.GetAll().Select(static entry => $"{Convert.ToHexString(entry.Key)}:{Convert.ToHexString(entry.Value)}")];

    private static int TopNibble(Address address) => Keccak.Compute(address.Bytes).Bytes[0] >> 4;

    private static int Nibbles(IEnumerable<AddressAsKey> addresses)
    {
        int mask = 0;
        foreach (AddressAsKey address in addresses) mask |= 1 << TopNibble(address.Value);
        return mask;
    }

    private static Address RandomAddress(Random rng, int nibbleMask)
    {
        while (true)
        {
            byte[] bytes = new byte[Address.Size];
            rng.NextBytes(bytes);
            Address address = new(bytes);
            if ((nibbleMask & (1 << TopNibble(address))) != 0) return address;
        }
    }

    private static Hash256 RandomHash(Random rng)
    {
        byte[] bytes = new byte[Hash256.Size];
        rng.NextBytes(bytes);
        return new Hash256(bytes);
    }

    private static Account RandomAccount(Random rng) => rng.Next(4) switch
    {
        0 => new Account((ulong)rng.Next(0, 1000), (UInt256)rng.Next(), RandomHash(rng), RandomHash(rng)),
        _ => new Account((ulong)rng.Next(0, 1000), (UInt256)rng.Next(1, int.MaxValue)),
    };

    private static Dictionary<AddressAsKey, Account?> NewAccounts(Random rng, int count, int nibbleMask)
    {
        Dictionary<AddressAsKey, Account?> accounts = [];
        while (accounts.Count < count) accounts[RandomAddress(rng, nibbleMask)] = RandomAccount(rng);
        return accounts;
    }

    private static Address Pick(Random rng, List<Address> addresses) => addresses[rng.Next(addresses.Count)];

    private static int RandomNibbleMask(Random rng, int nibbles)
    {
        int mask = 0;
        while (System.Numerics.BitOperations.PopCount((uint)mask) < nibbles) mask |= 1 << rng.Next(16);
        return mask;
    }

    /// <summary>A pre-block trie and a batch on it: creates, updates, deletes, no-op rewrites, emptied and totally empty
    /// accounts, deletes of absent keys, whole nibbles deleted, sometimes everything; over all nibbles or a few.</summary>
    private static void RandomBlock(int seed, out Dictionary<AddressAsKey, Account?> pre, out Dictionary<AddressAsKey, Account?> batch, out string shape)
    {
        Random rng = new(seed);
        int kind = rng.Next(5);
        int preMask = AllNibbles;
        int batchMask = AllNibbles;
        int preCount = rng.Next(0, 3001);
        switch (kind)
        {
            case 0:
                shape = "dense";
                break;
            case 1:
                shape = "sparse";
                preMask = RandomNibbleMask(rng, rng.Next(1, 5));
                batchMask = RandomNibbleMask(rng, rng.Next(1, 5));
                break;
            case 2:
                shape = "dense pre, sparse batch";
                batchMask = RandomNibbleMask(rng, rng.Next(1, 5));
                break;
            case 3:
                shape = "tiny pre";
                preCount = rng.Next(0, 4);
                break;
            default:
                shape = "one-nibble pre";
                preMask = 1 << rng.Next(16);
                preCount = rng.Next(1, 300);
                break;
        }

        pre = NewAccounts(rng, preCount, preMask);
        List<Address> existing = [.. pre.Keys.Select(static key => key.Value).Where(address => (batchMask & (1 << TopNibble(address))) != 0)];
        int batchCount = rng.Next(4) == 0 ? rng.Next(0, 200) : rng.Next(0, 3001);

        batch = [];
        for (int i = 0; i < batchCount; i++)
        {
            int op = rng.Next(100);
            if (existing.Count > 0 && op < 35) batch[Pick(rng, existing)] = RandomAccount(rng);
            else if (existing.Count > 0 && op < 45) batch[Pick(rng, existing)] = null;
            else if (existing.Count > 0 && op < 50)
            {
                Address address = Pick(rng, existing);
                batch[address] = pre[address];
            }
            else if (existing.Count > 0 && op < 55) batch[Pick(rng, existing)] = Account.TotallyEmpty;
            else if (op < 90) batch[RandomAddress(rng, batchMask)] = RandomAccount(rng);
            else if (op < 95) batch[RandomAddress(rng, batchMask)] = Account.TotallyEmpty;
            else batch[RandomAddress(rng, batchMask)] = null;
        }

        int extra = rng.Next(20);
        if (extra < 4 && pre.Count > 0)
        {
            int deleted = RandomNibbleMask(rng, rng.Next(1, 4));
            shape += ", whole nibbles deleted";
            foreach (AddressAsKey key in pre.Keys)
            {
                if ((deleted & (1 << TopNibble(key.Value))) != 0) batch[key] = null;
            }
        }
        else if (extra == 4 && pre.Count > 0)
        {
            shape += ", everything deleted";
            batch.Clear();
            foreach (AddressAsKey key in pre.Keys) batch[key] = null;
        }
    }

    private static void BuildEdge(Edge edge, out Dictionary<AddressAsKey, Account?> pre, out Dictionary<AddressAsKey, Account?> batch)
    {
        Random rng = new((int)edge + 7);
        pre = edge switch
        {
            Edge.CreatesOnEmptyTree => [],
            Edge.RootIsLeaf or Edge.RootIsLeafAndUpdated => NewAccounts(rng, 1, AllNibbles),
            Edge.RootIsExtension or Edge.RootIsExtensionOneNibbleBatch => NewAccounts(rng, 50, 1 << 5),
            Edge.EmptyResult or Edge.EmptyBatch or Edge.AbsentDeletesOnly or Edge.NoOpRewrites or Edge.TotallyEmptyAccounts => NewAccounts(rng, 500, AllNibbles),
            _ => NewAccounts(rng, 2000, AllNibbles),
        };

        List<Address> existing = [.. pre.Keys.Select(static key => key.Value)];
        batch = [];
        switch (edge)
        {
            case Edge.CreatesOnEmptyTree:
            case Edge.RootIsLeaf:
            case Edge.RootIsExtension:
                batch = NewAccounts(rng, 300, AllNibbles);
                break;
            case Edge.Updates:
                while (batch.Count < 300) batch[Pick(rng, existing)] = RandomAccount(rng);
                break;
            case Edge.Deletes:
                while (batch.Count < 300) batch[Pick(rng, existing)] = null;
                break;
            case Edge.WholeNibbleDeleted:
                foreach (Address address in existing.Where(static address => TopNibble(address) == 7)) batch[address] = null;
                while (batch.Count < 400)
                {
                    Address address = Pick(rng, existing);
                    if (TopNibble(address) != 7) batch[address] = RandomAccount(rng);
                }
                break;
            case Edge.AllButOneNibbleDeleted:
                foreach (Address address in existing.Where(static address => TopNibble(address) != 3)) batch[address] = null;
                break;
            case Edge.AllButOneAccountDeleted:
                foreach (Address address in existing.Skip(1)) batch[address] = null;
                break;
            case Edge.NibbleWithOneEntry:
                while (batch.Count < 300)
                {
                    Address address = Pick(rng, existing);
                    if (TopNibble(address) != 9) batch[address] = RandomAccount(rng);
                }
                batch[RandomAddress(rng, 1 << 9)] = RandomAccount(rng);
                break;
            case Edge.RootIsLeafAndUpdated:
                batch = NewAccounts(rng, 300, AllNibbles);
                batch[existing[0]] = RandomAccount(rng);
                break;
            case Edge.RootIsExtensionOneNibbleBatch:
                batch = NewAccounts(rng, 200, 1 << 5);
                for (int i = 0; i < 20; i++) batch[existing[i]] = RandomAccount(rng);
                break;
            case Edge.EmptyResult:
                foreach (Address address in existing) batch[address] = null;
                break;
            case Edge.EmptyBatch:
                break;
            case Edge.AbsentDeletesOnly:
                while (batch.Count < 200) batch[RandomAddress(rng, AllNibbles)] = null;
                break;
            case Edge.NoOpRewrites:
                while (batch.Count < 200)
                {
                    Address address = Pick(rng, existing);
                    batch[address] = pre[address];
                }
                break;
            case Edge.TotallyEmptyAccounts:
                while (batch.Count < 200) batch[RandomAddress(rng, AllNibbles)] = Account.TotallyEmpty;
                for (int i = 0; i < 100; i++) batch[existing[i]] = Account.TotallyEmpty;
                break;
        }
    }
}
