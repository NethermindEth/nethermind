// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using NUnit.Framework;
using Snappier;
using Transaction = Nethermind.BeaconChain.Types.Transaction;

namespace Nethermind.BeaconChain.Test.Storage;

/// <summary>
/// The root-to-children index behind <c>headers?parent_root</c>. Every expectation here is about
/// completeness, not just membership: a list the store calls complete must hold every child ever
/// stored, and every stored block's list must be complete, because the API turns "complete" into a
/// 200 and anything else into a 500.
/// </summary>
public class BeaconChainStoreChildrenIndexTests
{
    private MemColumnsDb<BeaconChainDbColumns> _db = null!;
    private BeaconChainStore _store = null!;

    [SetUp]
    public void CreateStore()
    {
        _db = new MemColumnsDb<BeaconChainDbColumns>();
        _store = new BeaconChainStore(_db);
    }

    [TearDown]
    public void DisposeStore() => _db.Dispose();

    [Test]
    public void A_block_stored_through_the_index_starts_with_a_complete_empty_child_list()
    {
        Hash256 root = TestRoot(1);
        _store.PutBlock(root, CreateBlock(100, parent: TestRoot(0)));

        Assert.That(_store.TryGetChildren(root, out Hash256[] children, out bool complete), Is.True);
        Assert.That(complete, Is.True, "nothing could have been stored under this block before it existed");
        Assert.That(children, Is.Empty);
    }

    [Test]
    public void Children_are_linked_to_their_parent_in_store_order_and_only_once()
    {
        Hash256 parent = TestRoot(1);
        Hash256 childA = TestRoot(2);
        Hash256 childB = TestRoot(3);
        _store.PutBlock(parent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(childA, CreateBlock(101, parent));
        _store.PutBlock(childB, CreateBlock(102, parent));
        _store.PutBlock(childA, CreateBlock(101, parent)); // a re-store must not duplicate the link

        Assert.That(_store.TryGetChildren(parent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(complete, Is.True);
        Assert.That(children, Is.EqualTo(new[] { childA, childB }));
    }

    [Test]
    public void A_parent_never_stored_through_the_index_is_tracked_but_reported_incomplete()
    {
        Hash256 legacyParent = TestRoot(1);
        WriteLegacyBlock(legacyParent, CreateBlock(100, parent: TestRoot(0)));
        Hash256 child = TestRoot(2);
        _store.PutBlock(child, CreateBlock(101, legacyParent));

        Assert.That(_store.TryGetBlock(legacyParent, out _), Is.True, "the legacy block itself is readable");
        Assert.That(_store.TryGetChildren(legacyParent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { child }), "the child stored through the index is known");
        Assert.That(complete, Is.False, "children stored before the index existed would be missing, so this list is not an answer");
    }

    [Test]
    public void Re_storing_a_stored_block_whose_entry_is_pending_completes_it_with_its_children()
    {
        Hash256 stored = TestRoot(1);
        Hash256 child = TestRoot(2);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(stored, block);
        _store.PutBlock(child, CreateBlock(101, stored));

        _store.PutBlock(stored, block);

        Assert.That(_store.TryGetChildren(stored, out Hash256[] children, out bool complete), Is.True);
        Assert.That(complete, Is.True, "a stored block's list is complete: opening the database rebuilds the index, so no stored block lives outside it");
        Assert.That(children, Is.EqualTo(new[] { child }));
    }

    [Test]
    public void Unknown_roots_have_no_entry()
    {
        Assert.That(_store.TryGetChildren(TestRoot(9), out Hash256[] children, out bool complete), Is.False);
        Assert.That(children, Is.Empty);
        Assert.That(complete, Is.False);
    }

    [Test]
    public void Deleting_a_block_unlinks_it_from_its_parent_and_drops_its_own_entry()
    {
        Hash256 parent = TestRoot(1);
        Hash256 childA = TestRoot(2);
        Hash256 childB = TestRoot(3);
        _store.PutBlock(parent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(childA, CreateBlock(101, parent));
        _store.PutBlock(childB, CreateBlock(102, parent));

        _store.DeleteBlock(childA);

        Assert.That(_store.TryGetBlock(childA, out _), Is.False);
        Assert.That(_store.TryGetChildren(childA, out _, out _), Is.False, "a deleted block keeps no entry");
        Assert.That(_store.TryGetChildren(parent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(complete, Is.True, "pruning a child does not make the parent's list incomplete: the pruned block is gone from this node entirely");
        Assert.That(children, Is.EqualTo(new[] { childB }));
    }

    [Test]
    public void Deleting_a_block_whose_parent_root_is_zero_unlinks_it_like_any_other_child()
    {
        Hash256 childA = TestRoot(2);
        Hash256 childB = TestRoot(3);
        _store.PutBlock(childA, CreateBlock(101, parent: Hash256.Zero));
        _store.PutBlock(childB, CreateBlock(102, parent: Hash256.Zero));

        _store.DeleteBlock(childA);

        Assert.That(_store.TryGetChildren(Hash256.Zero, out Hash256[] children, out _), Is.True);
        Assert.That(children, Is.EqualTo(new[] { childB }), "the zero root is a legal parent root, not a sentinel: its list must forget a deleted block like any other parent's");
    }

    [Test]
    public void A_block_stored_after_its_own_child_still_unlinks_from_its_parent_on_delete()
    {
        Hash256 grandparent = TestRoot(1);
        Hash256 parent = TestRoot(2);
        Hash256 child = TestRoot(3);
        _store.PutBlock(grandparent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(child, CreateBlock(102, parent)); // backfill order: the child's entry for its parent does not know the grandparent yet
        _store.PutBlock(parent, CreateBlock(101, grandparent));

        _store.DeleteBlock(parent);

        Assert.That(_store.TryGetChildren(grandparent, out Hash256[] children, out _), Is.True);
        Assert.That(children, Is.Empty, "the parent's own store must record the grandparent, or its later delete has nothing to unlink from");
    }

    [Test]
    public void Deleting_a_legacy_block_does_not_touch_its_parents_list()
    {
        Hash256 legacyParent = TestRoot(1);
        Hash256 legacyChild = TestRoot(2);
        Hash256 indexedChild = TestRoot(3);
        WriteLegacyBlock(legacyParent, CreateBlock(100, parent: TestRoot(0)));
        WriteLegacyBlock(legacyChild, CreateBlock(101, legacyParent));
        _store.PutBlock(indexedChild, CreateBlock(102, legacyParent));

        _store.DeleteBlock(legacyChild);

        Assert.That(_store.TryGetBlock(legacyChild, out _), Is.False);
        Assert.That(_store.TryGetChildren(legacyParent, out Hash256[] children, out _), Is.True);
        Assert.That(children, Is.EqualTo(new[] { indexedChild }), "a block that was never linked has nothing to unlink");
    }

    [Test]
    public void Deleting_a_block_that_still_has_stored_children_keeps_them_for_a_re_store()
    {
        Hash256 parent = TestRoot(1);
        Hash256 child = TestRoot(2);
        SignedBeaconBlock parentBlock = CreateBlock(100, parent: TestRoot(0));
        _store.PutBlock(parent, parentBlock);
        _store.PutBlock(child, CreateBlock(101, parent));

        _store.DeleteBlock(parent);

        Assert.That(_store.TryGetChildren(parent, out Hash256[] orphaned, out bool completeWhileDeleted), Is.True);
        Assert.That(orphaned, Is.EqualTo(new[] { child }), "the child is still stored, so the link must outlive its parent's deletion");
        Assert.That(completeWhileDeleted, Is.False);

        _store.PutBlock(parent, parentBlock);

        Assert.That(_store.TryGetChildren(parent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { child }), "a re-store that started an empty list would call it complete while a child sits in the store");
        Assert.That(complete, Is.True, "every child ever stored is listed, so the re-stored parent can vouch for it again");
    }

    [Test]
    public void Deleting_the_last_child_of_a_deleted_block_drops_the_dangling_entry()
    {
        Hash256 parent = TestRoot(1);
        Hash256 child = TestRoot(2);
        _store.PutBlock(parent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(child, CreateBlock(101, parent));

        _store.DeleteBlock(parent);
        _store.DeleteBlock(child);

        Assert.That(_store.TryGetChildren(parent, out _, out _), Is.False, "nothing is stored under the parent any more, so there is nothing to remember");
    }

    [Test]
    public void A_block_deleted_without_an_entry_is_complete_once_re_stored()
    {
        Hash256 root = TestRoot(1);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(root, block);

        _store.DeleteBlock(root);
        _store.PutBlock(root, block);

        Assert.That(_store.TryGetBlock(root, out _), Is.True);
        Assert.That(_store.TryGetChildren(root, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.Empty);
        Assert.That(complete, Is.True, "a delete must not leave a mark that keeps the re-stored block incomplete for good");
    }

    [Test]
    public void A_block_with_a_pending_entry_stays_complete_through_a_delete_and_re_store()
    {
        Hash256 stored = TestRoot(1);
        Hash256 indexedChild = TestRoot(2);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(stored, block);
        _store.PutBlock(indexedChild, CreateBlock(101, stored));

        _store.DeleteBlock(stored);
        _store.PutBlock(stored, block);

        Assert.That(_store.TryGetChildren(stored, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { indexedChild }), "the indexed child stays linked");
        Assert.That(complete, Is.True, "pruning a block must not pin its list incomplete: every child stored under it is listed");
    }

    [Test]
    public void Deleting_an_unknown_root_leaves_no_trace_in_the_index()
    {
        _store.DeleteBlock(TestRoot(9));

        Assert.That(_store.TryGetChildren(TestRoot(9), out _, out _), Is.False);
    }

    [Test]
    public void The_children_index_does_not_disturb_the_canonical_slot_index_in_the_same_column()
    {
        Hash256 parent = TestRoot(1);
        Hash256 child = TestRoot(2);
        _store.SetCanonicalRoot(100, parent);
        _store.PutBlock(parent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(child, CreateBlock(101, parent));
        _store.SetCanonicalRoot(101, child);

        Assert.That(_store.TryGetCanonicalRoot(100, out Hash256? at100), Is.True);
        Assert.That(at100, Is.EqualTo(parent));
        Assert.That(_store.TryGetCanonicalRoot(101, out Hash256? at101), Is.True);
        Assert.That(at101, Is.EqualTo(child));
        Assert.That(_store.TryGetChildren(parent, out Hash256[] children, out _), Is.True);
        Assert.That(children, Is.EqualTo(new[] { child }));
    }

    [Test]
    public void Opening_a_database_from_before_the_index_rebuilds_every_child_list_as_complete()
    {
        Hash256 grandparent = TestRoot(0);
        Hash256 parent = TestRoot(1);
        Hash256 legacyChild = TestRoot(2);
        Hash256 indexedChild = TestRoot(3);
        Hash256 deletedLegacy = TestRoot(4);
        WriteLegacyBlock(parent, CreateBlock(100, grandparent));
        WriteLegacyBlock(legacyChild, CreateBlock(101, parent));
        _store.PutBlock(indexedChild, CreateBlock(102, parent)); // a build that had the index but not yet the rebuild
        WriteLegacyBlock(deletedLegacy, CreateBlock(103, parent));
        _store.DeleteBlock(deletedLegacy); // leaves a tombstone that pins the block incomplete
        _store.SetCanonicalRoot(100, parent);
        _store.SetSchemaVersion(1);

        BeaconChainStore reopened = new(_db);
        reopened.EnsureSchemaVersion();

        Assert.That(reopened.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(BeaconChainStore.CurrentSchemaVersion));
        Assert.That(reopened.TryGetChildren(parent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EquivalentTo(new[] { legacyChild, indexedChild }), "the rebuild sees every stored block, indexed or not");
        Assert.That(complete, Is.True, "after the rebuild the list holds every stored child, so the API may answer with it");
        Assert.That(reopened.TryGetChildren(legacyChild, out Hash256[] leafChildren, out bool leafComplete), Is.True);
        Assert.That(leafChildren, Is.Empty);
        Assert.That(leafComplete, Is.True, "a legacy block with no stored children has a complete empty list, not a pinned-incomplete one");
        Assert.That(reopened.TryGetChildren(grandparent, out Hash256[] grandchildren, out bool grandparentComplete), Is.True);
        Assert.That(grandchildren, Is.EqualTo(new[] { parent }));
        Assert.That(grandparentComplete, Is.False, "a parent that is not stored is tracked for its children but cannot vouch for them");
        Assert.That(reopened.TryGetChildren(deletedLegacy, out _, out _), Is.False, "a tombstone describes a block the rebuild proves absent");
        Assert.That(reopened.TryGetCanonicalRoot(100, out Hash256? canonical), Is.True);
        Assert.That(canonical, Is.EqualTo(parent), "the canonical slot index shares the column and must survive the rebuild");
    }

    [Test]
    public void A_database_already_at_the_current_schema_version_keeps_its_index_as_is()
    {
        Hash256 legacy = TestRoot(1);
        WriteLegacyBlock(legacy, CreateBlock(100, parent: TestRoot(0)));
        _store.SetSchemaVersion(BeaconChainStore.CurrentSchemaVersion);

        BeaconChainStore reopened = new(_db);
        reopened.EnsureSchemaVersion();

        Assert.That(reopened.TryGetChildren(legacy, out _, out _), Is.False, "the rebuild scans every stored block, so it must run only on the upgrade that introduced it");
    }

    [Test]
    public void A_rebuilt_index_equals_the_one_built_by_storing_and_deleting_the_same_blocks()
    {
        const int Blocks = 2600;
        Random random = new(12345);
        List<Hash256> stored = [];
        List<Hash256> universe = [Hash256.Zero];
        for (int i = 1; i <= Blocks; i++)
        {
            Hash256 parent = i % 50 == 0 ? BlockRoot(100_000 + i) : universe[random.Next(universe.Count)];
            Hash256 root = BlockRoot(i);
            _store.PutBlock(root, CreateBlock((ulong)i, parent));
            stored.Add(root);
            universe.Add(root);
            universe.Add(parent);
        }

        foreach (Hash256 pruned in stored.Where((_, index) => index % 7 == 0).ToArray())
        {
            _store.DeleteBlock(pruned);
        }

        Dictionary<Hash256, (Hash256[] Children, bool Complete)> incremental = Snapshot(_store, universe);
        Assert.That(incremental.Values.Count(entry => entry.Complete), Is.GreaterThan(Blocks / 2), "the fixture must leave complete and pending entries to compare");
        _store.SetSchemaVersion(1);

        BeaconChainStore reopened = new(_db);
        reopened.EnsureSchemaVersion();

        Dictionary<Hash256, (Hash256[] Children, bool Complete)> rebuilt = Snapshot(reopened, universe);
        Assert.That(Blocks, Is.GreaterThan(BeaconChainStore.ChildrenRebuildBatchSize * 2), "the rebuild must cross batch boundaries");
        foreach (Hash256 root in universe.Distinct())
        {
            Assert.That(rebuilt[root].Complete, Is.EqualTo(incremental[root].Complete), $"completeness of {root}");
            Assert.That(rebuilt[root].Children, Is.EquivalentTo(incremental[root].Children), $"children of {root}");
        }
    }

    [Test]
    public void Rebuilding_a_wide_fan_out_of_large_blocks_allocates_far_less_than_the_blocks_it_reads()
    {
        const int Children = 3000;
        const int TransactionBytes = 64 * 1024;
        Hash256 parent = BlockRoot(0);
        byte[] record = Snappy.CompressToArray(SignedBeaconBlock.Encode(CreateBlock(100, parent, TransactionBytes)));
        IDb blocks = _db.GetColumnDb(BeaconChainDbColumns.Blocks);
        for (int i = 1; i <= Children; i++)
        {
            blocks.Set(BlockRoot(i).Bytes, record);
        }

        _store.SetSchemaVersion(1);
        BeaconChainStore reopened = new(_db);
        long before = GC.GetAllocatedBytesForCurrentThread();
        reopened.EnsureSchemaVersion();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(reopened.TryGetChildren(parent, out Hash256[] listed, out bool complete), Is.True);
        Assert.That(listed, Has.Length.EqualTo(Children));
        Assert.That(complete, Is.False, "the parent is not stored");
        long wholeBlocks = (long)Children * TransactionBytes;
        Assert.That(allocated, Is.LessThan(wholeBlocks / 16), "decompressing every whole block, or regrowing the parent list once per child, would allocate at least the blocks' size");
    }

    [TestCase(0, false)]
    [TestCase(100, false)]
    [TestCase(147, false)]
    [TestCase(148, false)]
    [TestCase(180, true)]
    public void Rebuilding_refuses_a_record_too_short_to_hold_a_parent_root(int decompressedLength, bool accepted)
    {
        Hash256 root = BlockRoot(1);
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(new byte[decompressedLength]));
        _store.SetSchemaVersion(1);
        BeaconChainStore reopened = new(_db);

        if (accepted)
        {
            reopened.EnsureSchemaVersion();
            Assert.That(reopened.TryGetChildren(Hash256.Zero, out Hash256[] children, out _), Is.True, "the parent root is the last byte range of the 148-byte prefix");
            Assert.That(children, Is.EqualTo(new[] { root }));
        }
        else if (decompressedLength == 148)
        {
            Assert.That(reopened.EnsureSchemaVersion, Throws.InstanceOf<System.IO.InvalidDataException>());
        }
        else
        {
            Assert.That(reopened.EnsureSchemaVersion, Throws.InstanceOf<InvalidOperationException>().With.Message.Contains(root.ToString()),
                "a parent root read past the record would link the block under a made-up parent");
        }
    }

    [Test]
    public void Deleting_a_block_whose_own_entry_is_unreadable_still_unlinks_it_from_its_parent()
    {
        Hash256 parent = TestRoot(1);
        Hash256 child = TestRoot(2);
        Hash256 sibling = TestRoot(3);
        _store.PutBlock(parent, CreateBlock(100, parent: TestRoot(0)));
        _store.PutBlock(child, CreateBlock(101, parent));
        _store.PutBlock(sibling, CreateBlock(102, parent));
        byte[] ownKey = [0x01, .. child.Bytes.ToArray()];
        _db.GetColumnDb(BeaconChainDbColumns.BlockIndex).Set(ownKey, [0x01, 0x02, 0x03]);

        _store.DeleteBlock(child);

        Assert.That(_store.TryGetChildren(parent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { sibling }), "a deleted block must not stay named in a list the API serves as complete");
        Assert.That(complete, Is.True);
    }

    /// <summary>Writes a block exactly as the pre-index store did: the compressed SSZ under the bare root, nothing else.</summary>
    private void WriteLegacyBlock(Hash256 root, SignedBeaconBlock block) =>
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

    private static Dictionary<Hash256, (Hash256[] Children, bool Complete)> Snapshot(BeaconChainStore store, IEnumerable<Hash256> roots)
    {
        Dictionary<Hash256, (Hash256[] Children, bool Complete)> snapshot = [];
        foreach (Hash256 root in roots.Distinct())
        {
            snapshot[root] = store.TryGetChildren(root, out Hash256[] children, out bool complete) ? (children, complete) : ([], false);
        }

        return snapshot;
    }

    private static Hash256 BlockRoot(int index)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), index);
        return new Hash256(bytes);
    }

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private static SignedBeaconBlock CreateBlock(ulong slot, Hash256 parent, int transactionBytes = 0)
    {
        SignedBeaconBlock block = SignedBeaconBlockBuilders.CreateMinimalBlock(slot);
        block.Message!.ParentRoot = parent;
        ExecutionPayload payload = block.Message.Body!.ExecutionPayload!;
        payload.BlockNumber = 1;
        payload.GasUsed = 0;
        payload.ExtraData = Bytes.FromHexString("0x");
        payload.Transactions = transactionBytes == 0 ? [] : [new Transaction { Bytes = new byte[transactionBytes] }];
        return block;
    }
}
