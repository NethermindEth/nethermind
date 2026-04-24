// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Test.Helpers;
using Nethermind.StateComposition.Visitors;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Diff;

[TestFixture]
public class TrieDiffWalkerTests
{
    private static Account CreateEOA(int balance = 100) => new(0, (UInt256)balance);

    private static Account CreateContract(Hash256 storageRoot, byte[]? code = null)
    {
        code ??= [0x60, 0x00];
        return new Account(0, 0, storageRoot, Keccak.Compute(code));
    }

    private static Account CreateContractNoStorage(byte[]? code = null)
    {
        code ??= [0x60, 0x00];
        return new Account(0, 0, Keccak.EmptyTreeHash, Keccak.Compute(code));
    }

    private static Hash256 CommitStorage(MemDb db, Address address, params (UInt256 Index, byte[] Value)[] slots)
    {
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        StorageTree storageTree = new(new RawScopedTrieStore(db, addressHash), LimboLogs.Instance);
        foreach ((UInt256 index, byte[]? value) in slots)
        {
            storageTree.Set(index, value);
        }
        storageTree.Commit();
        storageTree.UpdateRootHash();
        return storageTree.RootHash;
    }

    [Test]
    public void AddContractWithStorage_CountsStorageSlots()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]),
            ((UInt256)2, [3])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateContract(storageRoot));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsAdded, Is.EqualTo(1));
            Assert.That(diff.ContractsAdded, Is.EqualTo(1));
            Assert.That(diff.StorageSlotsAdded, Is.EqualTo(3));
            Assert.That(diff.StorageSlotsRemoved, Is.Zero);
            Assert.That(diff.NetStorageSlots, Is.EqualTo(3));
            Assert.That(diff.StorageTrieLeavesAdded, Is.EqualTo(3));
            Assert.That(diff.StorageTrieBytesAdded, Is.GreaterThan(0));
        }
    }

    [Test]
    public void ModifyStorageSlot_NetZeroSlots()
    {
        MemDb db = new();
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1]),
            (UInt256.One, [2])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        Hash256 addressHash = TestItem.AddressA.ToAccountPath.ToCommitment();
        StorageTree storageTree2 = new(new RawScopedTrieStore(db, addressHash), storageRoot1, LimboLogs.Instance);
        storageTree2.Set(UInt256.Zero, [42]);
        storageTree2.Commit();
        storageTree2.UpdateRootHash();
        Hash256 storageRoot2 = storageTree2.RootHash;

        tree.Set(TestItem.AddressA, CreateContract(storageRoot2));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);

        // Same slot modified → net zero (leaf at same path means update, not add/remove)
        Assert.That(diff.NetStorageSlots, Is.Zero);
    }

    [Test]
    public void AddStorageSlot_CountsOneSlotAdded()
    {
        MemDb db = new();
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        Hash256 addressHash = TestItem.AddressA.ToAccountPath.ToCommitment();
        StorageTree storageTree2 = new(new RawScopedTrieStore(db, addressHash), storageRoot1, LimboLogs.Instance);
        storageTree2.Set(UInt256.One, [2]);
        storageTree2.Commit();
        storageTree2.UpdateRootHash();
        Hash256 storageRoot2 = storageTree2.RootHash;

        tree.Set(TestItem.AddressA, CreateContract(storageRoot2));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.StorageSlotsAdded, Is.EqualTo(1));
            Assert.That(diff.NetStorageSlots, Is.EqualTo(1));
        }
    }

    [Test]
    public void ForwardAndReverse_AreSymmetric()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff forward = walker.ComputeDiff(root1, root2, resolver);
        TrieDiff reverse = walker.ComputeDiff(root2, root1, resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forward.AccountsAdded, Is.EqualTo(reverse.AccountsRemoved));
            Assert.That(forward.AccountsRemoved, Is.EqualTo(reverse.AccountsAdded));
            Assert.That(forward.ContractsAdded, Is.EqualTo(reverse.ContractsRemoved));
            Assert.That(forward.ContractsRemoved, Is.EqualTo(reverse.ContractsAdded));
            Assert.That(forward.AccountTrieBranchesAdded, Is.EqualTo(reverse.AccountTrieBranchesRemoved));
            Assert.That(forward.AccountTrieExtensionsAdded, Is.EqualTo(reverse.AccountTrieExtensionsRemoved));
            Assert.That(forward.AccountTrieLeavesAdded, Is.EqualTo(reverse.AccountTrieLeavesRemoved));
            Assert.That(forward.AccountTrieBytesAdded, Is.EqualTo(reverse.AccountTrieBytesRemoved));
        }
    }

    [Test]
    public void CumulativeTrieStats_ApplyDiff_RoundTrips()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        StateCompositionStats scan1 = v1.GetStats(1, root1);
        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(scan1);

        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Set(TestItem.AddressD, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);
        CumulativeTrieStats updated = cumulative.ApplyDiff(diff);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        StateCompositionStats scan2 = v2.GetStats(2, root2);
        CumulativeTrieStats expected = CumulativeTrieStats.FromScanStats(scan2);

        TestDataBuilders.AssertAccountTrieFieldsEqual(updated, expected);
    }

    [Test]
    public void MultiBlockIncremental_MatchesFullScan()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(v1.GetStats(1, root1));

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();

        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;
        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root1, root2, resolver));

        tree.Set(TestItem.AddressD, CreateContractNoStorage());
        tree.Set(TestItem.AddressA, CreateEOA(999));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root3 = tree.RootHash;
        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root2, root3, resolver));

        tree.Set(TestItem.AddressB, null);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root4 = tree.RootHash;
        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root3, root4, resolver));

        using StateCompositionVisitor v4 = new(LimboLogs.Instance);
        tree.Accept(v4, root4);
        CumulativeTrieStats expected = CumulativeTrieStats.FromScanStats(v4.GetStats(4, root4));

        TestDataBuilders.AssertAccountTrieFieldsEqual(cumulative, expected, "after 4 blocks");
    }

    [Test]
    public void StorageTrieIncremental_MatchesFullScan()
    {
        MemDb db = new();
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1]),
            (UInt256.One, [2])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(v1.GetStats(1, root1));

        Hash256 addressHash = TestItem.AddressA.ToAccountPath.ToCommitment();
        StorageTree storage2 = new(new RawScopedTrieStore(db, addressHash), storageRoot1, LimboLogs.Instance);
        storage2.Set((UInt256)2, [3]);
        storage2.Commit();
        storage2.UpdateRootHash();
        Hash256 storageRoot2 = storage2.RootHash;

        tree.Set(TestItem.AddressA, CreateContract(storageRoot2));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);
        CumulativeTrieStats updated = cumulative.ApplyDiff(diff);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        CumulativeTrieStats expected = CumulativeTrieStats.FromScanStats(v2.GetStats(2, root2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.StorageSlotsTotal, Is.EqualTo(expected.StorageSlotsTotal), "StorageSlotsTotal");
            Assert.That(updated.StorageTrieBranches, Is.EqualTo(expected.StorageTrieBranches), "StorageTrieBranches");
            Assert.That(updated.StorageTrieExtensions, Is.EqualTo(expected.StorageTrieExtensions), "StorageTrieExtensions");
            Assert.That(updated.StorageTrieLeaves, Is.EqualTo(expected.StorageTrieLeaves), "StorageTrieLeaves");
            Assert.That(updated.StorageTrieBytes, Is.EqualTo(expected.StorageTrieBytes), "StorageTrieBytes");
        }
    }

    [Test]
    public void LargeTrie_IncrementalMatchesFullScan()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        for (int i = 0; i < 50; i++)
        {
            byte[] addressBytes = new byte[20];
            addressBytes[0] = (byte)(i >> 8);
            addressBytes[1] = (byte)(i & 0xFF);
            Address addr = new(addressBytes);
            tree.Set(addr, CreateEOA(i + 1));
        }
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(v1.GetStats(1, root1));

        for (int i = 50; i < 60; i++)
        {
            byte[] addressBytes = new byte[20];
            addressBytes[0] = (byte)(i >> 8);
            addressBytes[1] = (byte)(i & 0xFF);
            Address addr = new(addressBytes);
            tree.Set(addr, CreateEOA(i + 1));
        }
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver);
        CumulativeTrieStats updated = cumulative.ApplyDiff(diff);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        CumulativeTrieStats expected = CumulativeTrieStats.FromScanStats(v2.GetStats(2, root2));

        TestDataBuilders.AssertAccountTrieFieldsEqual(updated, expected);
    }

    [Test]
    public void CumulativeTrieStats_FromScanStats_MapsCorrectly()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root = tree.RootHash;

        using StateCompositionVisitor visitor = new(LimboLogs.Instance);
        tree.Accept(visitor, root);
        StateCompositionStats scan = visitor.GetStats(1, root);

        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(scan);

        using (Assert.EnterMultipleScope())
        {
            // Verify mapping: FullNodes→Branches, (ShortNodes-ValueNodes)→Extensions, ValueNodes→Leaves
            Assert.That(cumulative.AccountsTotal, Is.EqualTo(scan.AccountsTotal));
            Assert.That(cumulative.ContractsTotal, Is.EqualTo(scan.ContractsTotal));
            Assert.That(cumulative.StorageSlotsTotal, Is.EqualTo(scan.StorageSlotsTotal));
            Assert.That(cumulative.AccountTrieBranches, Is.EqualTo(scan.AccountTrieFullNodes));
            Assert.That(cumulative.AccountTrieExtensions, Is.EqualTo(scan.AccountTrieShortNodes - scan.AccountTrieValueNodes));
            Assert.That(cumulative.AccountTrieLeaves, Is.EqualTo(scan.AccountTrieValueNodes));
            Assert.That(cumulative.AccountTrieBytes, Is.EqualTo(scan.AccountTrieNodeBytes));
            Assert.That(cumulative.StorageTrieBranches, Is.EqualTo(scan.StorageTrieFullNodes));
            Assert.That(cumulative.StorageTrieExtensions, Is.EqualTo(scan.StorageTrieShortNodes - scan.StorageTrieValueNodes));
            Assert.That(cumulative.StorageTrieLeaves, Is.EqualTo(scan.StorageTrieValueNodes));
            Assert.That(cumulative.StorageTrieBytes, Is.EqualTo(scan.StorageTrieNodeBytes));
        }
    }

    private static Address AddressFromSeed(int seed) =>
        new(Keccak.Compute(BitConverter.GetBytes(seed)).Bytes[..20].ToArray());

    [Test]
    public void MultiBlock_ScanDiffScan_CumulativeMatchesFreshScan()
    {
        const int totalBlocks = 20;
        const int newEOAsPerBlock = 500;
        const int newContractsPerBlock = 50;
        const int slotsPerContract = 5;
        const int modifiedEOAsPerBlock = 100;
        const int modifiedContractsPerBlock = 10;

        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        Hash256[] roots = new Hash256[totalBlocks];
        List<Address> eoaAddresses = [];
        List<Address> contractAddresses = [];
        Dictionary<Address, Hash256> contractStorageRoots = [];
        Random rng = new(42); // deterministic seed
        int addressSeed = 0;

        // --- Generate blocks ---
        for (int block = 0; block < totalBlocks; block++)
        {
            for (int i = 0; i < newEOAsPerBlock; i++)
            {
                Address addr = AddressFromSeed(addressSeed++);
                tree.Set(addr, CreateEOA(rng.Next(1, 10000)));
                eoaAddresses.Add(addr);
            }

            for (int i = 0; i < newContractsPerBlock; i++)
            {
                Address addr = AddressFromSeed(addressSeed++);
                (UInt256, byte[])[] slots = new (UInt256, byte[])[slotsPerContract];
                for (int s = 0; s < slotsPerContract; s++)
                {
                    byte[] val = new byte[32];
                    rng.NextBytes(val);
                    slots[s] = ((UInt256)(s + 1), val);
                }
                Hash256 sroot = CommitStorage(db, addr, slots);
                tree.Set(addr, CreateContract(sroot));
                contractAddresses.Add(addr);
                contractStorageRoots[addr] = sroot;
            }

            if (block > 0)
            {
                int pool = eoaAddresses.Count - newEOAsPerBlock; // exclude just-added
                for (int i = 0; i < modifiedEOAsPerBlock && pool > 0; i++)
                {
                    int idx = rng.Next(0, pool);
                    tree.Set(eoaAddresses[idx], CreateEOA(rng.Next(1, 10000)));
                }
            }

            if (block >= 2)
            {
                int pool = contractAddresses.Count - newContractsPerBlock;
                int numModify = Math.Min(modifiedContractsPerBlock, pool);
                for (int i = 0; i < numModify; i++)
                {
                    int idx = rng.Next(0, pool);
                    Address addr = contractAddresses[idx];
                    Hash256 existingRoot = contractStorageRoots[addr];
                    Hash256 addrHash = addr.ToAccountPath.ToCommitment();
                    StorageTree st = new(new RawScopedTrieStore(db, addrHash), existingRoot, LimboLogs.Instance);
                    byte[] val = new byte[32];
                    rng.NextBytes(val);
                    st.Set((UInt256)(slotsPerContract + block), val);
                    st.Commit();
                    st.UpdateRootHash();
                    contractStorageRoots[addr] = st.RootHash;
                    tree.Set(addr, CreateContract(st.RootHash));
                }
            }

            tree.Commit();
            tree.UpdateRootHash();
            roots[block] = tree.RootHash;

            TestContext.Out.WriteLine(
                $"Block {block,2}: EOAs={eoaAddresses.Count,6}, Contracts={contractAddresses.Count,5}, root={roots[block].ToString()[..16]}...");
        }

        TestContext.Out.WriteLine(
            $"\nState generated: {eoaAddresses.Count} EOAs, {contractAddresses.Count} contracts, {totalBlocks} blocks\n");

        // --- Verification: scan(X) + diffs(X→Y) == scan(Y) ---
        (int from, int to)[] ranges = [(0, 5), (0, 10), (0, 19), (5, 15), (10, 19), (0, 1), (18, 19)];

        foreach ((int from, int to) in ranges)
        {
            // 1. Full scan at 'from'
            StateCompositionVisitor v1 = new(LimboLogs.Instance);
            tree.Accept(v1, roots[from]);
            StateCompositionStats s1 = v1.GetStats(from, roots[from]);
            CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(s1);
            v1.Dispose();

            // 2. Incremental diffs from→to
            RawScopedTrieStore resolver = new(db);
            TrieDiffWalker walker = new();
            for (int b = from; b < to; b++)
            {
                TrieDiff diff = walker.ComputeDiff(roots[b], roots[b + 1], resolver);
                cumulative = cumulative.ApplyDiff(diff);
            }

            // 3. Full scan at 'to'
            StateCompositionVisitor v2 = new(LimboLogs.Instance);
            tree.Accept(v2, roots[to]);
            StateCompositionStats s2 = v2.GetStats(to, roots[to]);
            CumulativeTrieStats expected = CumulativeTrieStats.FromScanStats(s2);
            v2.Dispose();

            // CodeBytesTotal and SlotCountHistogram are frozen by design —
            // ApplyDiff leaves them at their last-scan values, so record
            // equality must ignore them here. Normalize them on `cumulative`
            // so the assertion covers only diff-tracked fields.
            cumulative = cumulative with
            {
                CodeBytesTotal = expected.CodeBytesTotal,
                SlotCountHistogram = expected.SlotCountHistogram,
            };

            // 4. Compare
            bool match = cumulative == expected;

            string status = match ? "PASS" : "FAIL";
            TestContext.Out.WriteLine($"[{from,2} → {to,2}]  {status}  accounts={cumulative.AccountsTotal} contracts={cumulative.ContractsTotal} storage={cumulative.StorageSlotsTotal}");

            if (!match)
            {
                string[] fields =
                [
                    $"  AccountsTotal:       cum={cumulative.AccountsTotal} exp={expected.AccountsTotal} Δ={cumulative.AccountsTotal - expected.AccountsTotal}",
                    $"  ContractsTotal:      cum={cumulative.ContractsTotal} exp={expected.ContractsTotal} Δ={cumulative.ContractsTotal - expected.ContractsTotal}",
                    $"  StorageSlotsTotal:   cum={cumulative.StorageSlotsTotal} exp={expected.StorageSlotsTotal} Δ={cumulative.StorageSlotsTotal - expected.StorageSlotsTotal}",
                    $"  AcctTrieBranches:    cum={cumulative.AccountTrieBranches} exp={expected.AccountTrieBranches} Δ={cumulative.AccountTrieBranches - expected.AccountTrieBranches}",
                    $"  AcctTrieExtensions:  cum={cumulative.AccountTrieExtensions} exp={expected.AccountTrieExtensions} Δ={cumulative.AccountTrieExtensions - expected.AccountTrieExtensions}",
                    $"  AcctTrieLeaves:      cum={cumulative.AccountTrieLeaves} exp={expected.AccountTrieLeaves} Δ={cumulative.AccountTrieLeaves - expected.AccountTrieLeaves}",
                    $"  AcctTrieBytes:       cum={cumulative.AccountTrieBytes} exp={expected.AccountTrieBytes} Δ={cumulative.AccountTrieBytes - expected.AccountTrieBytes}",
                    $"  StorTrieBranches:    cum={cumulative.StorageTrieBranches} exp={expected.StorageTrieBranches} Δ={cumulative.StorageTrieBranches - expected.StorageTrieBranches}",
                    $"  StorTrieExtensions:  cum={cumulative.StorageTrieExtensions} exp={expected.StorageTrieExtensions} Δ={cumulative.StorageTrieExtensions - expected.StorageTrieExtensions}",
                    $"  StorTrieLeaves:      cum={cumulative.StorageTrieLeaves} exp={expected.StorageTrieLeaves} Δ={cumulative.StorageTrieLeaves - expected.StorageTrieLeaves}",
                    $"  StorTrieBytes:       cum={cumulative.StorageTrieBytes} exp={expected.StorageTrieBytes} Δ={cumulative.StorageTrieBytes - expected.StorageTrieBytes}",
                ];
                foreach (string f in fields)
                    TestContext.Out.WriteLine(f);
            }

            Assert.That(cumulative, Is.EqualTo(expected), $"scan({from}) + diffs({from}→{to}) must equal scan({to})");
        }
    }

    /// <summary>
    /// Applies a forward diff then a backward diff and asserts the result is
    /// field-for-field equal to the original baseline. Exercises the subtraction
    /// path in ApplyDiff that fires only during reorg rollback.
    /// </summary>
    [Test]
    public void ReorgRollback_ForwardThenBackward_RestoresBaseline()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressD, CreateEOA(400));
        tree.Set(TestItem.AddressE, CreateEOA(500));
        tree.Set(TestItem.AddressA, CreateEOA(999));
        tree.Set(TestItem.AddressB, null);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeTrieStats baseline = CumulativeTrieStats.FromScanStats(v1.GetStats(1, root1));

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();

        TrieDiff forward = walker.ComputeDiff(root1, root2, resolver);
        CumulativeTrieStats updated = baseline.ApplyDiff(forward);

        // Backward diff root2 → root1 (reorg rollback)
        TrieDiff backward = walker.ComputeDiff(root2, root1, resolver);
        CumulativeTrieStats final = updated.ApplyDiff(backward);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(final.AccountsTotal, Is.EqualTo(baseline.AccountsTotal), "AccountsTotal");
            Assert.That(final.ContractsTotal, Is.EqualTo(baseline.ContractsTotal), "ContractsTotal");
            Assert.That(final.StorageSlotsTotal, Is.EqualTo(baseline.StorageSlotsTotal), "StorageSlotsTotal");
            Assert.That(final.AccountTrieBranches, Is.EqualTo(baseline.AccountTrieBranches), "AccountTrieBranches");
            Assert.That(final.AccountTrieExtensions, Is.EqualTo(baseline.AccountTrieExtensions), "AccountTrieExtensions");
            Assert.That(final.AccountTrieLeaves, Is.EqualTo(baseline.AccountTrieLeaves), "AccountTrieLeaves");
            Assert.That(final.AccountTrieBytes, Is.EqualTo(baseline.AccountTrieBytes), "AccountTrieBytes");
            Assert.That(final.ContractsWithStorage, Is.EqualTo(baseline.ContractsWithStorage), "ContractsWithStorage");
            Assert.That(final.EmptyAccounts, Is.EqualTo(baseline.EmptyAccounts), "EmptyAccounts");
        }
    }

}
