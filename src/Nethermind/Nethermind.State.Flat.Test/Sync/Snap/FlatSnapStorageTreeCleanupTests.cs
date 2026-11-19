// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

/// <summary>
/// Boundary cleanup over a real flat persistence, with stale data that is only a trie node: cleanup runs only where the
/// batch's probe finds data, so these fail if the probe misses a node the walk would delete.
/// </summary>
[TestFixture]
public class FlatSnapStorageTreeCleanupTests
{
    // Storage keys keep the first four bytes of the address hash in front and the rest after the path.
    private static readonly ValueHash256 AccountA = new("0x11223344aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ValueHash256 SlotKey = new("0x5555555555555555555555555555555555555555555555555555555555555555");
    private static readonly byte[] StaleRlp = [0xc1, 0x11];

    // The first 20 nibbles of SlotKey, a path long enough for the fallback column.
    private static readonly TreePath LongPathOnSlotKey = TreePath.FromHexString(new string('5', 20));

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void DeleteOutsideChildren_OfAnExtension_DeletesAStaleNodeAtItsCompressedPath()
    {
        TreePath stale = TreePath.FromHexString("1");
        Write(batch => batch.SetStorageTrieNode(AccountA, stale, StaleRlp));
        Assert.That(HasNode(AccountA, stale), Is.True, "guards the premise: the stale node is stored");
        TrieNode extension = TrieNodeFactory.CreateExtension([1, 2], new TrieNode(NodeType.Unknown, Keccak.Compute("child"u8)));

        using (FlatSnapStorageTree tree = NewTree())
        {
            tree.DeleteOutsideChildren(TreePath.Empty, extension, ValueKeccak.Zero, ValueKeccak.MaxValue);
            tree.Commit(ValueKeccak.Zero, ValueKeccak.MaxValue);
        }

        Assert.That(HasNode(AccountA, stale), Is.False);
    }

    [Test]
    public void DeleteOutsideCompleteTree_OfARootLeaf_DeletesAStaleLongPathNodeOnItsKey()
    {
        TreePath stale = LongPathOnSlotKey;
        Write(batch => batch.SetStorageTrieNode(AccountA, stale, StaleRlp));
        Assert.That(HasNode(AccountA, stale), Is.True, "guards the premise: the stale node is stored");

        using (FlatSnapStorageTree tree = NewTree())
        {
            tree.BulkSetAndUpdateRootHash([new PathWithStorageSlot(SlotKey, Rlp.Encode(new byte[] { 1 }))]);
            tree.DeleteOutsideCompleteTree(ValueKeccak.Zero, ValueKeccak.MaxValue);
            tree.Commit(ValueKeccak.Zero, ValueKeccak.MaxValue);
        }

        Assert.That(HasNode(AccountA, stale), Is.False);
    }

    // Accounts that share the first four bytes of their hash share the key prefix, so the probe must match the rest. Snap
    // sync probes through an atomic batch, so that batch must answer as the plain one does.
    [Test]
    public void IsStorageRangeEmpty_DoesNotSeeTheDataOfAnAccountSharingTheKeyPrefix([Values] bool atomic)
    {
        ValueHash256 withSlot = new("0x11223344bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ValueHash256 withShortNode = new("0x11223344cccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        ValueHash256 withLongNode = new("0x11223344dddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        Write(batch =>
        {
            batch.SetStorageRawEncoded(withSlot, SlotKey, Rlp.Encode(new byte[] { 1 }));
            batch.SetStorageTrieNode(withShortNode, TreePath.FromHexString("1"), StaleRlp);
            batch.SetStorageTrieNode(withLongNode, LongPathOnSlotKey, StaleRlp);
        });

        using IPersistence.IWriteBatch probe = atomic
            ? _persistence.CreateAtomicWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL)
            : _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.IsStorageRangeEmpty(AccountA, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.True, "the account itself");
            Assert.That(probe.IsStorageRangeEmpty(withSlot, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.False, "slot");
            Assert.That(probe.IsStorageRangeEmpty(withShortNode, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.False, "short-path node");
            Assert.That(probe.IsStorageRangeEmpty(withLongNode, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.False, "long-path node");
        }
    }

    // Commit publishes the cleanup; disposing an atomic batch without it would drop the deletes.
    private FlatSnapStorageTree NewTree() =>
        new(_persistence.CreateReader(), _persistence.CreateAtomicWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL),
            AccountA.ToCommitment(), enableDoubleWriteCheck: false, LimboLogs.Instance);

    private void Write(Action<IPersistence.IWriteBatch> write)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        write(batch);
    }

    private bool HasNode(in ValueHash256 account, in TreePath path)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.TryLoadStorageRlp(account.ToCommitment(), path, ReadFlags.None, static (_, _) => { }, 0);
    }
}
