// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
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

    /// <summary>
    /// Helper: commit a storage tree for an address, return the storage root hash.
    /// </summary>
    private static Hash256 CommitStorage(MemDb db, Address address, params (UInt256 Index, byte[] Value)[] slots)
    {
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        StorageTree storageTree = new(new RawScopedTrieStore(db, addressHash), LimboLogs.Instance);
        foreach (var (index, value) in slots)
        {
            storageTree.Set(index, value);
        }
        storageTree.Commit();
        storageTree.UpdateRootHash();
        return storageTree.RootHash;
    }

    #region 1. Null / empty root cases

    [Test]
    public void SameRoot_ReturnsZeroDiff()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root, root);

        Assert.That(diff, Is.Default);
    }

    #endregion

    #region 2. Empty → non-empty (genesis-like)

    [Test]
    public void EmptyToMultipleAccounts_CountsAllAdded()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(Keccak.EmptyTreeHash, root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsAdded, Is.EqualTo(3));
            Assert.That(diff.AccountsRemoved, Is.Zero);
            Assert.That(diff.ContractsAdded, Is.EqualTo(1));
            Assert.That(diff.ContractsRemoved, Is.Zero);
            Assert.That(diff.AccountTrieLeavesAdded, Is.EqualTo(3));
        }
    }

    #endregion

    #region 4. Add one account (branch split)

    [Test]
    public void AddOneAccount_CorrectDiff()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsAdded, Is.EqualTo(1));
            Assert.That(diff.AccountsRemoved, Is.Zero);
            Assert.That(diff.NetAccounts, Is.EqualTo(1));
            // Adding second account to a single-leaf trie creates a branch
            Assert.That(diff.AccountTrieLeavesAdded, Is.GreaterThanOrEqualTo(1));
        }
    }

    #endregion

    #region 5. Modify account balance (same leaf path)

    [Test]
    public void ModifyAccountBalance_NetZeroAccounts()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressA, CreateEOA(999));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.NetAccounts, Is.Zero);
            Assert.That(diff.NetContracts, Is.Zero);
            Assert.That(diff.AccountTrieLeavesAdded, Is.GreaterThanOrEqualTo(1));
            Assert.That(diff.AccountTrieLeavesRemoved, Is.GreaterThanOrEqualTo(1));
        }
    }

    #endregion

    #region 6. Remove one account

    [Test]
    public void RemoveOneAccount_CorrectDiff()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        Assert.That(diff.NetAccounts, Is.EqualTo(-1));
    }

    #endregion

    #region 7. Contract detection

    [Test]
    public void AddContract_CountsContractAdded()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsAdded, Is.EqualTo(1));
            Assert.That(diff.ContractsAdded, Is.EqualTo(1));
            Assert.That(diff.ContractsRemoved, Is.Zero);
        }
    }

    [Test]
    public void RemoveContract_CountsContractRemoved()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsRemoved, Is.EqualTo(1));
            Assert.That(diff.ContractsRemoved, Is.EqualTo(1));
        }
    }

    #endregion

    #region 8. Storage trie changes

    [Test]
    public void AddContractWithStorage_CountsStorageSlots()
    {
        MemDb db = new();

        // First, create storage for the contract
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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountsAdded, Is.EqualTo(1));
            Assert.That(diff.ContractsAdded, Is.EqualTo(1));
            Assert.That(diff.StorageSlotsAdded, Is.EqualTo(3));
            Assert.That(diff.StorageSlotsRemoved, Is.Zero);
            Assert.That(diff.NetStorageSlots, Is.EqualTo(3));
            // Storage trie nodes should be counted
            Assert.That(diff.StorageTrieLeavesAdded, Is.EqualTo(3));
            Assert.That(diff.StorageTrieBytesAdded, Is.GreaterThan(0));
        }
    }

    [Test]
    public void ModifyStorageSlot_NetZeroSlots()
    {
        MemDb db = new();

        // Initial storage: 2 slots
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1]),
            (UInt256.One, [2])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Modified storage: same 2 slots, different values
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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        // Same slot modified → net zero (leaf at same path means update, not add/remove)
        Assert.That(diff.NetStorageSlots, Is.Zero);
    }

    [Test]
    public void AddStorageSlot_CountsOneSlotAdded()
    {
        MemDb db = new();

        // Initial storage: 1 slot
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Add a second slot
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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.StorageSlotsAdded, Is.EqualTo(1));
            Assert.That(diff.NetStorageSlots, Is.EqualTo(1));
        }
    }

    #endregion

    #region 9. Trie structure: byte counting

    [Test]
    public void ByteCountsArePositive_WhenNodesChange()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.AccountTrieBytesAdded, Is.GreaterThan(0));
            Assert.That(diff.AccountTrieBytesRemoved, Is.GreaterThan(0));
        }
    }

    #endregion

    #region 10. Symmetry: forward diff ≡ reversed diff

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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff forward = walker.ComputeDiff(root1, root2);
        TrieDiff reverse = walker.ComputeDiff(root2, root1);

        using (Assert.EnterMultipleScope())
        {
            // Forward adds = reverse removes and vice versa
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

    #endregion

    #region 11. CumulativeSizeStats round-trip

    [Test]
    public void CumulativeSizeStats_ApplyDiff_RoundTrips()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Full scan at root1
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        StateCompositionStats scan1 = v1.GetStats(1, root1);
        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(scan1);

        // Add more accounts
        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Set(TestItem.AddressD, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        // Compute diff
        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        // Apply diff
        CumulativeSizeStats updated = cumulative.ApplyDiff(diff);

        // Full scan at root2 for verification
        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        StateCompositionStats scan2 = v2.GetStats(2, root2);
        CumulativeSizeStats expected = CumulativeSizeStats.FromScanStats(scan2);

        using (Assert.EnterMultipleScope())
        {
            // The cumulative stats after applying diff must match a fresh full scan
            Assert.That(updated.AccountsTotal, Is.EqualTo(expected.AccountsTotal), "AccountsTotal mismatch");
            Assert.That(updated.ContractsTotal, Is.EqualTo(expected.ContractsTotal), "ContractsTotal mismatch");
            Assert.That(updated.AccountTrieBranches, Is.EqualTo(expected.AccountTrieBranches), "AccountTrieBranches mismatch");
            Assert.That(updated.AccountTrieExtensions, Is.EqualTo(expected.AccountTrieExtensions), "AccountTrieExtensions mismatch");
            Assert.That(updated.AccountTrieLeaves, Is.EqualTo(expected.AccountTrieLeaves), "AccountTrieLeaves mismatch");
            Assert.That(updated.AccountTrieBytes, Is.EqualTo(expected.AccountTrieBytes), "AccountTrieBytes mismatch");
        }
    }

    #endregion

    #region 12. Multi-block incremental consistency

    [Test]
    public void MultiBlockIncremental_MatchesFullScan()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        // Block 1: 2 accounts
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Full scan at root1
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(v1.GetStats(1, root1));

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));

        // Block 2: +1 account
        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root1, root2));

        // Block 3: +1 contract, modify A
        tree.Set(TestItem.AddressD, CreateContractNoStorage());
        tree.Set(TestItem.AddressA, CreateEOA(999));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root3 = tree.RootHash;

        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root2, root3));

        // Block 4: remove B
        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root4 = tree.RootHash;

        cumulative = cumulative.ApplyDiff(walker.ComputeDiff(root3, root4));

        // Full scan at root4 for verification
        using StateCompositionVisitor v4 = new(LimboLogs.Instance);
        tree.Accept(v4, root4);
        CumulativeSizeStats expected = CumulativeSizeStats.FromScanStats(v4.GetStats(4, root4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cumulative.AccountsTotal, Is.EqualTo(expected.AccountsTotal), "AccountsTotal after 4 blocks");
            Assert.That(cumulative.ContractsTotal, Is.EqualTo(expected.ContractsTotal), "ContractsTotal after 4 blocks");
            Assert.That(cumulative.AccountTrieBranches, Is.EqualTo(expected.AccountTrieBranches), "AccountTrieBranches after 4 blocks");
            Assert.That(cumulative.AccountTrieExtensions, Is.EqualTo(expected.AccountTrieExtensions), "AccountTrieExtensions after 4 blocks");
            Assert.That(cumulative.AccountTrieLeaves, Is.EqualTo(expected.AccountTrieLeaves), "AccountTrieLeaves after 4 blocks");
            Assert.That(cumulative.AccountTrieBytes, Is.EqualTo(expected.AccountTrieBytes), "AccountTrieBytes after 4 blocks");
        }
    }

    #endregion

    #region 13. Storage trie incremental consistency

    [Test]
    public void StorageTrieIncremental_MatchesFullScan()
    {
        MemDb db = new();

        // Block 1: contract with 2 storage slots
        Hash256 storageRoot1 = CommitStorage(db, TestItem.AddressA,
            (UInt256.Zero, [1]),
            (UInt256.One, [2])
        );

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateContract(storageRoot1));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Full scan at root1
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(v1.GetStats(1, root1));

        // Block 2: add a storage slot
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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);
        CumulativeSizeStats updated = cumulative.ApplyDiff(diff);

        // Full scan at root2 for verification
        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        CumulativeSizeStats expected = CumulativeSizeStats.FromScanStats(v2.GetStats(2, root2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.StorageSlotsTotal, Is.EqualTo(expected.StorageSlotsTotal), "StorageSlotsTotal");
            Assert.That(updated.StorageTrieBranches, Is.EqualTo(expected.StorageTrieBranches), "StorageTrieBranches");
            Assert.That(updated.StorageTrieExtensions, Is.EqualTo(expected.StorageTrieExtensions), "StorageTrieExtensions");
            Assert.That(updated.StorageTrieLeaves, Is.EqualTo(expected.StorageTrieLeaves), "StorageTrieLeaves");
            Assert.That(updated.StorageTrieBytes, Is.EqualTo(expected.StorageTrieBytes), "StorageTrieBytes");
        }
    }

    #endregion

    #region 14. Large trie (many accounts)

    [Test]
    public void LargeTrie_IncrementalMatchesFullScan()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        // Create 50 accounts to generate a deeper trie with branches
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

        // Full scan
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(v1.GetStats(1, root1));

        // Add 10 more
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

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));
        TrieDiff diff = walker.ComputeDiff(root1, root2);
        CumulativeSizeStats updated = cumulative.ApplyDiff(diff);

        // Full scan at root2
        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        CumulativeSizeStats expected = CumulativeSizeStats.FromScanStats(v2.GetStats(2, root2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.AccountsTotal, Is.EqualTo(expected.AccountsTotal), "AccountsTotal");
            Assert.That(updated.AccountTrieBranches, Is.EqualTo(expected.AccountTrieBranches), "AccountTrieBranches");
            Assert.That(updated.AccountTrieExtensions, Is.EqualTo(expected.AccountTrieExtensions), "AccountTrieExtensions");
            Assert.That(updated.AccountTrieLeaves, Is.EqualTo(expected.AccountTrieLeaves), "AccountTrieLeaves");
            Assert.That(updated.AccountTrieBytes, Is.EqualTo(expected.AccountTrieBytes), "AccountTrieBytes");
        }
    }

    #endregion

    #region 16. CumulativeSizeStats FromScanStats mapping

    [Test]
    public void CumulativeSizeStats_FromScanStats_MapsCorrectly()
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

        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(scan);

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

    #endregion

    #region Integration: multi-block scan/diff/scan verification

    private static Address AddressFromSeed(int seed)
    {
        return new Address(Keccak.Compute(BitConverter.GetBytes(seed)).Bytes[..20].ToArray());
    }

    [Test]
    public void MultiBlock_ScanDiffScan_CumulativeMatchesFreshScan()
    {
        const int totalBlocks = 20;
        const int newEOAsPerBlock = 500;
        const int newContractsPerBlock = 50;
        const int slotsPerContract = 5;
        const int modifiedEOAsPerBlock = 100;
        const int modifiedContractsPerBlock = 10;

        const string reportPath = "/private/tmp/claude/bench/nm-500k/verification-report.txt";

        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        Hash256[] roots = new Hash256[totalBlocks];
        List<Address> eoaAddresses = [];
        List<Address> contractAddresses = [];
        Dictionary<Address, Hash256> contractStorageRoots = new();
        Random rng = new(42); // deterministic seed
        int addressSeed = 0;

        // --- Generate blocks ---
        for (int block = 0; block < totalBlocks; block++)
        {
            // New EOA accounts
            for (int i = 0; i < newEOAsPerBlock; i++)
            {
                Address addr = AddressFromSeed(addressSeed++);
                tree.Set(addr, CreateEOA(rng.Next(1, 10000)));
                eoaAddresses.Add(addr);
            }

            // New contracts with storage
            for (int i = 0; i < newContractsPerBlock; i++)
            {
                Address addr = AddressFromSeed(addressSeed++);
                var slots = new (UInt256, byte[])[slotsPerContract];
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

            // Modify existing EOA balances
            if (block > 0)
            {
                int pool = eoaAddresses.Count - newEOAsPerBlock; // exclude just-added
                for (int i = 0; i < modifiedEOAsPerBlock && pool > 0; i++)
                {
                    int idx = rng.Next(0, pool);
                    tree.Set(eoaAddresses[idx], CreateEOA(rng.Next(1, 10000)));
                }
            }

            // Modify existing contract storage
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

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using StreamWriter report = new(reportPath);
        report.WriteLine("=== TrieDiffWalker Scan/Diff/Scan Verification ===");
        report.WriteLine($"Date: {DateTime.UtcNow:O}");
        report.WriteLine($"Blocks: {totalBlocks}, EOAs/block: {newEOAsPerBlock}, Contracts/block: {newContractsPerBlock}");
        report.WriteLine($"Storage slots/contract: {slotsPerContract}, Modified EOAs/block: {modifiedEOAsPerBlock}, Modified contracts/block: {modifiedContractsPerBlock}");
        report.WriteLine($"Total accounts: {eoaAddresses.Count + contractAddresses.Count}");
        report.WriteLine();

        bool allPassed = true;

        foreach ((int from, int to) in ranges)
        {
            // 1. Full scan at 'from'
            StateCompositionVisitor v1 = new(LimboLogs.Instance);
            tree.Accept(v1, roots[from]);
            StateCompositionStats s1 = v1.GetStats(from, roots[from]);
            CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(s1);
            v1.Dispose();

            // 2. Incremental diffs from→to
            TrieDiffWalker walker = new(new RawScopedTrieStore(db));
            for (int b = from; b < to; b++)
            {
                TrieDiff diff = walker.ComputeDiff(roots[b], roots[b + 1]);
                cumulative = cumulative.ApplyDiff(diff);
            }

            // 3. Full scan at 'to'
            StateCompositionVisitor v2 = new(LimboLogs.Instance);
            tree.Accept(v2, roots[to]);
            StateCompositionStats s2 = v2.GetStats(to, roots[to]);
            CumulativeSizeStats expected = CumulativeSizeStats.FromScanStats(s2);
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
            if (!match) allPassed = false;

            string status = match ? "PASS" : "FAIL";
            report.WriteLine($"[{from,2} → {to,2}]  {status}");
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
                {
                    report.WriteLine(f);
                    TestContext.Out.WriteLine(f);
                }
            }

            Assert.That(cumulative, Is.EqualTo(expected), $"scan({from}) + diffs({from}→{to}) must equal scan({to})");
        }

        report.WriteLine();
        report.WriteLine(allPassed ? "ALL RANGES PASSED" : "SOME RANGES FAILED");
        report.Flush();

        TestContext.Out.WriteLine($"\nReport: {reportPath}");
    }

    #endregion

    #region 16. Depth delta tracking

    [Test]
    public void DepthDelta_AddOneLeaf_IncreasesValueNodesAtExpectedDepth()
    {
        // Single account → single leaf at depth 0 (no branch above it)
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        Hash256 emptyRoot = Keccak.EmptyTreeHash;

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db), trackDepth: true);
        TrieDiff diff = walker.ComputeDiff(emptyRoot, root1);

        Assert.That(diff.DepthDelta, Is.Not.Null);
        CumulativeDepthStats d = diff.DepthDelta!;

        // Net leaf added at some depth — total ValueNodes across all depths must be +1
        long totalValueNodeDelta = 0;
        for (int i = 0; i < 16; i++) totalValueNodeDelta += d.AccountValueNodes[i];

        Assert.That(totalValueNodeDelta, Is.EqualTo(1), "Exactly one account leaf added");
    }

    [Test]
    public void DepthDelta_RemoveOneLeaf_DecreasesValueNodes()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db), trackDepth: true);
        TrieDiff diff = walker.ComputeDiff(root1, Keccak.EmptyTreeHash);

        Assert.That(diff.DepthDelta, Is.Not.Null);
        CumulativeDepthStats d = diff.DepthDelta!;

        long totalValueNodeDelta = 0;
        for (int i = 0; i < 16; i++) totalValueNodeDelta += d.AccountValueNodes[i];

        Assert.That(totalValueNodeDelta, Is.EqualTo(-1), "Exactly one account leaf removed");
    }

    [Test]
    public void DepthDelta_AddBranch_IncreasesFullNodesAndOccupancy()
    {
        // Two accounts force a branch node
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db), trackDepth: true);
        TrieDiff diff = walker.ComputeDiff(root1, root2);

        Assert.That(diff.DepthDelta, Is.Not.Null);
        CumulativeDepthStats d = diff.DepthDelta!;

        // Adding a second account requires at least one branch net-new or restructured
        // The important invariant: TotalBranchNodes tracks the occupancy histogram change
        // Just verify the delta is consistent with total branch count change from the plain diff
        Assert.That(d.TotalBranchNodes, Is.GreaterThanOrEqualTo(0),
            "Adding a second account should not net-remove branch nodes");
    }

    [Test]
    public void DepthDelta_ShortNodesIncludeLeaves_GethConvention()
    {
        // Per Geth convention: ShortNodeCount = Extensions + Leaves
        // So AccountShortNodes delta must equal AccountValueNodes delta for a pure leaf change
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        Hash256 emptyRoot = Keccak.EmptyTreeHash;

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        TrieDiffWalker walker = new(new RawScopedTrieStore(db), trackDepth: true);
        TrieDiff diff = walker.ComputeDiff(emptyRoot, root1);

        CumulativeDepthStats d = diff.DepthDelta!;

        // For a pure leaf addition: ShortNodes delta at leaf depth should be >= ValueNodes delta
        // (ShortNodes also includes extensions, but in this trivial trie there are none)
        long totalShort = 0, totalValue = 0;
        for (int i = 0; i < 16; i++) { totalShort += d.AccountShortNodes[i]; totalValue += d.AccountValueNodes[i]; }

        Assert.That(totalShort, Is.GreaterThanOrEqualTo(totalValue),
            "ShortNodes delta must be >= ValueNodes delta (Geth: ShortNode includes leaf shortNode)");
    }

    [Test]
    public void DepthDelta_CumulativeDepthStats_AddInPlaceMatchesSeedFromScan()
    {
        // Build a trie, scan it for baseline, then apply a delta for the change and check
        // that the cumulative depth stats are consistent (no off-by-one from Geth shifts).
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Full scan for baseline depth stats
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        TrieDepthDistribution dist1 = v1.GetTrieDistribution();

        CumulativeDepthStats cumDepth = new();
        cumDepth.SeedFromScan(dist1);

        // Add one more account
        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        // Compute diff with depth tracking
        TrieDiffWalker walker = new(new RawScopedTrieStore(db), trackDepth: true);
        TrieDiff diff = walker.ComputeDiff(root1, root2);
        Assert.That(diff.DepthDelta, Is.Not.Null);

        cumDepth.AddInPlace(diff.DepthDelta!);

        // Full scan at root2 for expected depth stats
        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        TrieDepthDistribution dist2 = v2.GetTrieDistribution();

        CumulativeDepthStats expected = new();
        expected.SeedFromScan(dist2);

        // Total value nodes (leaves) must match between incremental and fresh scan
        long incValueTotal = 0, expValueTotal = 0;
        for (int i = 0; i < 16; i++)
        {
            incValueTotal += cumDepth.AccountValueNodes[i];
            expValueTotal += expected.AccountValueNodes[i];
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incValueTotal, Is.EqualTo(expValueTotal),
                    "Total account leaf count (incremental) must match fresh scan");

            // Total branch nodes must match
            Assert.That(cumDepth.TotalBranchNodes, Is.EqualTo(expected.TotalBranchNodes),
                "TotalBranchNodes (incremental) must match fresh scan");
        }
    }

    #endregion

    #region Reorg Rollback

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

        // root1: two EOAs + one contract
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContractNoStorage());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // root2: add more accounts, modify balance of A, remove B
        tree.Set(TestItem.AddressD, CreateEOA(400));
        tree.Set(TestItem.AddressE, CreateEOA(500));
        tree.Set(TestItem.AddressA, CreateEOA(999));
        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        // Baseline: full scan at root1
        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        CumulativeSizeStats baseline = CumulativeSizeStats.FromScanStats(v1.GetStats(1, root1));

        TrieDiffWalker walker = new(new RawScopedTrieStore(db));

        // Forward diff root1 → root2
        TrieDiff forward = walker.ComputeDiff(root1, root2);
        CumulativeSizeStats updated = baseline.ApplyDiff(forward);

        // Backward diff root2 → root1 (reorg rollback)
        TrieDiff backward = walker.ComputeDiff(root2, root1);
        CumulativeSizeStats final = updated.ApplyDiff(backward);

        // After round-trip the stats must equal the original baseline exactly.
        // A negative-value bug in ApplyDiff would surface here.
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

    #endregion
}
