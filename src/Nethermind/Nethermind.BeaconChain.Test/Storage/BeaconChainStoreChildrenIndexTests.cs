// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Snappier;
using Transaction = Nethermind.BeaconChain.Types.Transaction;

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.Storage;

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

    private enum IndexAction
    {
        Store,
        Legacy,
        Delete,
        CorruptOwnEntry,
        Children,
        Block,
        SetCanonical,
        Canonical,
    }
    private readonly record struct IndexStep(IndexAction Action, byte Root, byte Parent = 0, byte[]? Children = null, bool? Complete = null, bool Exists = true);
    private sealed record IndexCase(string Name, IndexStep[] Steps);
    private static IndexStep Put(byte root, byte parent = 0, bool legacy = false) => new(legacy ? IndexAction.Legacy : IndexAction.Store, root, parent);
    private static IndexStep Delete(byte root) => new(IndexAction.Delete, root);
    private static IndexStep Children(byte root, byte[]? children = null, bool? complete = null, bool exists = true) => new(IndexAction.Children, root, Children: children, Complete: complete, Exists: exists);
    private static IndexStep Block(byte root, bool exists) => new(IndexAction.Block, root, Exists: exists);
    private static IndexStep Canonical(byte root, bool set = false) => new(set ? IndexAction.SetCanonical : IndexAction.Canonical, root);
    private static readonly IndexCase[] IndexScenarios =
    [
        new("A_block_stored_through_the_index_starts_with_a_complete_empty_child_list", [Put(1), Children(1, [], true)]),
        new("Children_are_linked_to_their_parent_in_store_order_and_only_once", [Put(1), Put(2, 1), Put(3, 1), Put(2, 1), Children(1, [2, 3], true)]),
        new("A_parent_never_stored_through_the_index_is_tracked_but_reported_incomplete", [Put(1, legacy: true), Put(2, 1), Block(1, true), Children(1, [2], false)]),
        new("Re_storing_a_stored_block_whose_entry_is_pending_completes_it_with_its_children", [Put(1, legacy: true), Put(2, 1), Put(1), Children(1, [2], true)]),
        new("Unknown_roots_have_no_entry", [Children(9, [], false, false)]),
        new("Deleting_a_block_whose_own_entry_is_unreadable_still_unlinks_it_from_its_parent", [Put(1), Put(2, 1), Put(3, 1), new(IndexAction.CorruptOwnEntry, 2), Delete(2), Children(1, [3], true)]),
        new("Deleting_a_block_unlinks_it_from_its_parent_and_drops_its_own_entry", [Put(1), Put(2, 1), Put(3, 1), Delete(2), Block(2, false), Children(2, exists: false), Children(1, [3], true)]),
        new("Deleting_a_block_whose_parent_root_is_zero_unlinks_it_like_any_other_child", [Put(2), Put(3), Delete(2), Children(0, [3])]),
        new("A_block_stored_after_its_own_child_still_unlinks_from_its_parent_on_delete", [Put(1), Put(3, 2), Put(2, 1), Delete(2), Children(1, [])]),
        new("Deleting_a_legacy_block_does_not_touch_its_parents_list", [Put(1, legacy: true), Put(2, 1, true), Put(3, 1), Delete(2), Block(2, false), Children(1, [3])]),
        new("Deleting_a_block_that_still_has_stored_children_keeps_them_for_a_re_store", [Put(1), Put(2, 1), Delete(1), Children(1, [2], false), Put(1), Children(1, [2], true)]),
        new("Deleting_the_last_child_of_a_deleted_block_drops_the_dangling_entry", [Put(1), Put(2, 1), Delete(1), Delete(2), Children(1, exists: false)]),
        new("A_block_deleted_without_an_entry_is_complete_once_re_stored", [Put(1, legacy: true), Delete(1), Put(1), Block(1, true), Children(1, [], true)]),
        new("A_block_with_a_pending_entry_stays_complete_through_a_delete_and_re_store", [Put(1, legacy: true), Put(2, 1), Delete(1), Put(1), Children(1, [2], true)]),
        new("Deleting_an_unknown_root_leaves_no_trace_in_the_index", [Delete(9), Children(9, exists: false)]),
        new("The_children_index_does_not_disturb_the_canonical_slot_index_in_the_same_column", [Canonical(1, true), Put(1), Put(2, 1), Canonical(2, true), Canonical(1), Canonical(2), Children(1, [2])]),
    ];
    private static IEnumerable<TestCaseData> IndexCases()
    {
        for (int i = 0; i < IndexScenarios.Length; i++) yield return new TestCaseData(i).SetName(IndexScenarios[i].Name);
    }

    [TestCaseSource(nameof(IndexCases))]
    public void Children_index_preserves_order_completeness_and_deletion_lifecycle(int index)
    {
        foreach (IndexStep step in IndexScenarios[index].Steps)
        {
            Hash256 root = FromLow(step.Root);
            ulong slot = 99UL + step.Root;
            switch (step.Action)
            {
                case IndexAction.Store:
                    _store.PutBlock(root, CreateBlock(slot, FromLow(step.Parent)));
                    break;
                case IndexAction.Legacy:
                    WriteLegacyBlock(root, CreateBlock(slot, FromLow(step.Parent)));
                    break;
                case IndexAction.Delete:
                    _store.DeleteBlock(root);
                    break;
                case IndexAction.CorruptOwnEntry:
                    _db.GetColumnDb(BeaconChainDbColumns.BlockIndex).Set([0x01, .. root.Bytes.ToArray()], [0x01, 0x02, 0x03]);
                    break;
                case IndexAction.Block:
                    Assert.That(_store.TryGetForkedBlock(root, out _), Is.EqualTo(step.Exists));
                    break;
                case IndexAction.Children:
                    Assert.That(_store.TryGetChildren(root, out Hash256[] children, out bool complete), Is.EqualTo(step.Exists));
                    if (step.Children is not null) Assert.That(children, Is.EqualTo(step.Children.Select(marker => FromLow(marker)).ToArray()));
                    if (step.Complete is { } expected) Assert.That(complete, Is.EqualTo(expected));
                    break;
                case IndexAction.SetCanonical:
                    _store.SetCanonicalRoot(slot, root);
                    break;
                case IndexAction.Canonical:
                    Assert.That(_store.TryGetCanonicalRoot(slot, out Hash256? canonical), Is.True);
                    Assert.That(canonical, Is.EqualTo(root));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }
    }

    [Test]
    public void Opening_a_database_from_before_the_index_rebuilds_every_child_list_as_complete()
    {
        Hash256 grandparent = FromLow(0);
        Hash256 parent = FromLow(1);
        Hash256 legacyChild = FromLow(2);
        Hash256 indexedChild = FromLow(3);
        Hash256 deletedLegacy = FromLow(4);
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
        Hash256 legacy = FromLow(1);
        WriteLegacyBlock(legacy, CreateBlock(100, parent: FromLow(0)));
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

    // Legacy record: compressed SSZ under bare block root, without index entries.
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

    private static Hash256 BlockRoot(int index) => BeaconChainStoreStateSlotIndexTests.Root(index);

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
