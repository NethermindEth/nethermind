// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Storage;

[TestFixture]
public class BeaconChainStoreStatePruningTests
{
    private const int StateSlotOffset = 40;

    private MemColumnsDb<BeaconChainDbColumns> _db = null!;
    private BeaconChainStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemColumnsDb<BeaconChainDbColumns>();
        _store = new BeaconChainStore(_db);
        _store.EnsureSchemaVersion();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();
    private static Hash256 Root(byte fill) => new(Enumerable.Repeat(fill, Hash256.Size).ToArray());

    internal static byte[] StateAt(ulong slot, int length = 256)
    {
        byte[] ssz = new byte[length];
        new Random((int)slot).NextBytes(ssz);
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(StateSlotOffset), slot);
        return ssz;
    }

    private void Put(byte rootFill, ulong slot, int length = 256) => _store.PutState(Root(rootFill), StateAt(slot, length));
    private bool Has(byte rootFill) => _store.TryGetState(Root(rootFill), out _);
    private int StateKeyCount() => _db.GetColumnDb(BeaconChainDbColumns.States).GetAllKeys().Count();

    [Test]
    public void Advancing_the_anchor_deletes_snapshots_old_anchors_and_candidates_at_or_below_it_and_keeps_the_rest()
    {
        Put(1, slot: 0);
        _store.SetAnchor(Root(1), 0);
        Put(2, slot: 32);
        Put(3, slot: 64);
        Put(4, slot: 96);
        Put(5, slot: 96);
        Put(6, slot: 130);
        Put(7, slot: 200);

        _store.SetAnchor(Root(4), 96);

        Assert.That(new[] { Has(1), Has(2), Has(3), Has(4), Has(5), Has(6), Has(7) },
            Is.EqualTo(new[] { false, false, false, true, false, true, true }),
            "old anchor, snapshot, candidate and a fork state at the anchor slot go; the anchor and everything above stay");
        Assert.That(_store.TryGetAnchor(out Hash256? root, out ulong slot), Is.True);
        Assert.That((root, slot), Is.EqualTo((Root(4), 96ul)));
    }

    [TestCase(true, 10ul, 50ul, 10 * 1024 * 1024 + 5, TestName = "A_deleted_multi_chunk_state_leaves_no_manifest_or_chunk_behind")]
    [TestCase(false, 5ul, 6ul, 9 * 1024 * 1024, TestName = "DeleteState_removes_one_state_and_ignores_a_root_with_none")]
    public void Removing_a_multi_chunk_state_keeps_only_the_other_states_manifest_and_chunk(bool prune, ulong removedSlot, ulong keptSlot, int length)
    {
        Put(1, removedSlot, length);
        Put(2, keptSlot);
        int keysWithLargeState = prune ? StateKeyCount() : 0;
        if (prune)
        {
            _store.SetAnchor(Root(2), keptSlot);
            Assert.That(keysWithLargeState, Is.GreaterThan(4), "fixture bug: the large state must span chunks");
        }
        else
        {
            _store.DeleteState(Root(1));
            _store.DeleteState(Root(3));
            Assert.That(new[] { Has(1), Has(2) }, Is.EqualTo(new[] { false, true }));
        }

        Assert.That(StateKeyCount(), Is.EqualTo(2), prune ? "only the anchor's manifest and its one chunk remain" : null);
    }

    [Test]
    public void States_above_the_anchor_survive_however_many_are_persisted_under_non_finality()
    {
        Put(1, slot: 32);
        _store.SetAnchor(Root(1), 32);
        for (byte i = 0; i < 20; i++)
        {
            Put((byte)(10 + i), slot: 64 + 32ul * i);
        }

        _store.SetAnchor(Root(1), 32);

        Assert.That(Enumerable.Range(10, 20).All(i => Has((byte)i)), Is.True);
        Assert.That(Has(1), Is.True);
    }

    [Test]
    public void Pruning_again_at_the_same_anchor_changes_nothing()
    {
        Put(1, slot: 5);
        Put(2, slot: 40);
        Put(3, slot: 90);
        _store.SetAnchor(Root(2), 40);
        int keysAfterFirst = StateKeyCount();

        _store.SetAnchor(Root(2), 40);

        Assert.That(StateKeyCount(), Is.EqualTo(keysAfterFirst));
        Assert.That(new[] { Has(1), Has(2), Has(3) }, Is.EqualTo(new[] { false, true, true }));
    }

    // An empty rewrite leaves old chunk 0; the manifest determines whether a slot exists.
    [TestCase(false, TestName = "A_state_with_no_readable_slot_is_kept")]
    [TestCase(true, TestName = "An_empty_state_rewritten_over_a_stale_chunk_is_kept")]
    public void A_state_without_a_readable_slot_is_kept(bool rewrite)
    {
        if (rewrite)
        {
            Put(1, slot: 10);
        }

        _store.PutState(Root(1), rewrite ? [] : [9]);
        Put(2, slot: 40);

        _store.SetAnchor(Root(2), 40);

        Assert.That(Has(1), Is.True);
    }

    [Test]
    public void The_anchor_and_its_state_load_from_a_reopened_store_after_pruning()
    {
        byte[] anchorState = StateAt(64, 5 * 1024 * 1024);
        Put(1, slot: 0);
        _store.SetAnchor(Root(1), 0);
        Put(2, slot: 32);
        _store.PutState(Root(3), anchorState);
        Put(4, slot: 100);
        _store.SetAnchor(Root(3), 64);

        BeaconChainStore reopened = new(_db);
        reopened.EnsureSchemaVersion();

        Assert.That(reopened.TryGetAnchor(out Hash256? root, out ulong slot), Is.True);
        Assert.That((root, slot), Is.EqualTo((Root(3), 64ul)));
        Assert.That(reopened.TryGetState(root!, out byte[]? loaded), Is.True);
        Assert.That(loaded, Is.EqualTo(anchorState));
        Assert.That(reopened.TryGetState(Root(4), out _), Is.True, "a state above the anchor is still there to replay from");
        Assert.That(reopened.TryGetState(Root(1), out _) || reopened.TryGetState(Root(2), out _), Is.False);
    }
}
