// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.Storage;

/// <summary>
/// The root-to-children index behind <c>headers?parent_root</c>. Every expectation here is about
/// completeness, not just membership: a list the store calls complete must hold every child ever
/// stored, and a parent whose children could have been stored without the index must never be
/// reported complete, because the API turns "complete" into a 200 and anything else into a 501.
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
    public void Re_storing_a_legacy_block_does_not_promote_it_to_complete()
    {
        Hash256 legacy = TestRoot(1);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(legacy, block);

        _store.PutBlock(legacy, block);

        Assert.That(_store.TryGetChildren(legacy, out _, out bool complete), Is.True);
        Assert.That(complete, Is.False, "a block that existed before the index may have children the index never saw");
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
        Assert.That(_store.TryGetChildren(legacyParent, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { indexedChild }), "a block that was never linked has nothing to unlink");
        Assert.That(complete, Is.False, "the legacy parent stays incomplete: the deleted legacy child is exactly the kind of child it never indexed");
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
    public void A_deleted_legacy_block_is_never_reported_complete_once_re_stored()
    {
        Hash256 legacy = TestRoot(1);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(legacy, block);

        _store.DeleteBlock(legacy);
        _store.PutBlock(legacy, block);

        Assert.That(_store.TryGetBlock(legacy, out _), Is.True);
        Assert.That(_store.TryGetChildren(legacy, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.Empty);
        Assert.That(complete, Is.False, "children stored beside it before the index existed are unknowable; a delete must not launder that into a fresh complete list");
    }

    [Test]
    public void A_legacy_block_with_indexed_children_stays_incomplete_through_a_delete_and_re_store()
    {
        Hash256 legacy = TestRoot(1);
        Hash256 indexedChild = TestRoot(2);
        SignedBeaconBlock block = CreateBlock(100, parent: TestRoot(0));
        WriteLegacyBlock(legacy, block);
        _store.PutBlock(indexedChild, CreateBlock(101, legacy));

        _store.DeleteBlock(legacy);
        _store.PutBlock(legacy, block);

        Assert.That(_store.TryGetChildren(legacy, out Hash256[] children, out bool complete), Is.True);
        Assert.That(children, Is.EqualTo(new[] { indexedChild }), "the indexed child stays linked");
        Assert.That(complete, Is.False, "the pending entry its child created is not proof that no legacy sibling exists");
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

    /// <summary>Writes a block exactly as the pre-index store did: the compressed SSZ under the bare root, nothing else.</summary>
    private void WriteLegacyBlock(Hash256 root, SignedBeaconBlock block) =>
        _db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

    private static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    private static SignedBeaconBlock CreateBlock(ulong slot, Hash256 parent) => new()
    {
        Message = new BeaconBlock
        {
            Slot = slot,
            ProposerIndex = 21,
            ParentRoot = parent,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBody
            {
                Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512) },
                ExecutionPayload = new ExecutionPayload
                {
                    ParentHash = Hash256.Zero,
                    FeeRecipient = Address.Zero,
                    StateRoot = Hash256.Zero,
                    ReceiptsRoot = Hash256.Zero,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = Hash256.Zero,
                    BlockNumber = 1,
                    GasLimit = 30_000_000,
                    GasUsed = 0,
                    Timestamp = 1_750_000_000,
                    ExtraData = Bytes.FromHexString("0x"),
                    BaseFeePerGas = 7,
                    BlockHash = Hash256.Zero,
                    Transactions = [],
                    Withdrawals = [],
                    BlobGasUsed = 0,
                    ExcessBlobGas = 0,
                },
                BlsToExecutionChanges = [],
                BlobKzgCommitments = [],
                ExecutionRequests = new ExecutionRequests { Deposits = [], Withdrawals = [], Consolidations = [] },
            },
        },
    };
}
