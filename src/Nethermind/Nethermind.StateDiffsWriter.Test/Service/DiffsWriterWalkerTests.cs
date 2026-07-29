// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateDiff.Core.Data;
using Nethermind.StateDiff.Core.Diff;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.StateDiffsWriter.Test.Service;

/// <summary>Walker behaviour tests for the slim two-list output plus the byte / leaf deltas.</summary>
[TestFixture]
public class DiffsWriterWalkerTests
{
    private static Account CreateEOA(int balance = 100) => new(0, (UInt256)balance);

    private static Account CreateContract(Hash256 storageRoot, byte[]? code = null)
    {
        code ??= [0x60, 0x00];
        return new Account(0, 0, storageRoot, Keccak.Compute(code));
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
    public void AddContractWithStorage_EmitsSlotCountDelta()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]),
            ((UInt256)2, [3]));

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
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver, resolver);

        Hash256 expectedAddressHash = TestItem.AddressB.ToAccountPath.ToCommitment();

        Assert.That(diff.SlotCountChanges, Has.Count.EqualTo(1));
        Assert.That(diff.SlotCountChanges[0].AddressHash, Is.EqualTo(expectedAddressHash.ValueHash256));
        Assert.That(diff.SlotCountChanges[0].SlotDelta, Is.EqualTo(3));
        Assert.That(diff.CodeHashChanges, Has.Count.EqualTo(1));
        Assert.That(diff.CodeHashChanges[0].AddressHash, Is.EqualTo(expectedAddressHash.ValueHash256));
        Assert.That(diff.CodeHashChanges[0].OldCodeHash, Is.EqualTo(CodeHashChange.NoCode));
        Assert.That(diff.CodeHashChanges[0].NewCodeHash, Is.Not.EqualTo(CodeHashChange.NoCode));
    }

    [Test]
    public void RemoveContractWithStorage_EmitsNegativeSlotDelta()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]));

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateContract(storageRoot));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver, resolver);

        Hash256 expectedAddressHash = TestItem.AddressB.ToAccountPath.ToCommitment();
        Assert.That(diff.SlotCountChanges, Has.Count.EqualTo(1));
        Assert.That(diff.SlotCountChanges[0].AddressHash, Is.EqualTo(expectedAddressHash.ValueHash256));
        Assert.That(diff.SlotCountChanges[0].SlotDelta, Is.EqualTo(-2));
        Assert.That(diff.CodeHashChanges, Has.Count.EqualTo(1));
        Assert.That(diff.CodeHashChanges[0].NewCodeHash, Is.EqualTo(CodeHashChange.NoCode));
    }

    [Test]
    public void EqualRoots_EmitsEmptyDiff()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root, root, resolver, resolver);

        Assert.That(diff.SlotCountChanges, Is.Empty);
        Assert.That(diff.CodeHashChanges, Is.Empty);
    }

    [Test]
    public void ForwardAndReverse_SlotDeltasAreOpposite()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB, (UInt256.Zero, [1]), (UInt256.One, [2]));

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
        TrieDiff forward = walker.ComputeDiff(root1, root2, resolver, resolver);
        TrieDiff reverse = walker.ComputeDiff(root2, root1, resolver, resolver);

        Assert.That(forward.SlotCountChanges[0].SlotDelta, Is.EqualTo(-reverse.SlotCountChanges[0].SlotDelta));
    }

    /// <summary>Pins the legacy two-list outputs so byte-tracking changes can't drift them, and checks the new fields.</summary>
    [Test]
    public void AddContractWithStorage_PreservesLegacyOutputsAndPopulatesNewDeltas()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]),
            ((UInt256)2, [3]));

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
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver, resolver);

        Hash256 expectedAddressHash = TestItem.AddressB.ToAccountPath.ToCommitment();
        Assert.That(diff.SlotCountChanges, Has.Count.EqualTo(1));
        Assert.That(diff.SlotCountChanges[0].AddressHash, Is.EqualTo(expectedAddressHash.ValueHash256));
        Assert.That(diff.SlotCountChanges[0].SlotDelta, Is.EqualTo(3));
        Assert.That(diff.CodeHashChanges, Has.Count.EqualTo(1));

        Assert.That(diff.AccountTrieBytesDelta, Is.GreaterThan(0));
        Assert.That(diff.StorageTrieBytesDelta, Is.GreaterThan(0));
        Assert.That(diff.AccountsAddedDelta, Is.EqualTo(1));
    }

    [Test]
    public void RemoveContractWithStorage_NegativeByteAndAccountDeltas()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]));

        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateContract(storageRoot));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressB, null!);
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root1, root2, resolver, resolver);

        Assert.That(diff.AccountsAddedDelta, Is.EqualTo(-1));
        Assert.That(diff.StorageTrieBytesDelta, Is.LessThan(0));
        // Account trie can shift either way when a leaf disappears (extension collapse, branch demotion).
        Assert.That(diff.SlotCountChanges, Has.Count.EqualTo(1));
        Assert.That(diff.SlotCountChanges[0].SlotDelta, Is.EqualTo(-2));
        Assert.That(diff.CodeHashChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void EqualRoots_AllNewDeltasAreZero()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root = tree.RootHash;

        RawScopedTrieStore resolver = new(db);
        TrieDiffWalker walker = new();
        TrieDiff diff = walker.ComputeDiff(root, root, resolver, resolver);

        Assert.That(diff.AccountTrieBytesDelta, Is.Zero);
        Assert.That(diff.StorageTrieBytesDelta, Is.Zero);
        Assert.That(diff.AccountsAddedDelta, Is.Zero);
    }

    [Test]
    public void ForwardAndReverse_NewDeltasAreOpposite()
    {
        MemDb db = new();
        Hash256 storageRoot = CommitStorage(db, TestItem.AddressB,
            (UInt256.Zero, [1]),
            (UInt256.One, [2]));

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
        TrieDiff forward = walker.ComputeDiff(root1, root2, resolver, resolver);
        TrieDiff reverse = walker.ComputeDiff(root2, root1, resolver, resolver);

        Assert.That(forward.AccountTrieBytesDelta, Is.EqualTo(-reverse.AccountTrieBytesDelta));
        Assert.That(forward.StorageTrieBytesDelta, Is.EqualTo(-reverse.StorageTrieBytesDelta));
        Assert.That(forward.AccountsAddedDelta, Is.EqualTo(-reverse.AccountsAddedDelta));
    }
}
