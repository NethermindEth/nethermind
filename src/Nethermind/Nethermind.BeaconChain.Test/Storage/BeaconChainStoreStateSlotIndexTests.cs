// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using static Nethermind.BeaconChain.Test.Storage.BeaconChainStoreStatePruningTests;

namespace Nethermind.BeaconChain.Test.Storage;

[TestFixture]
public class BeaconChainStoreStateSlotIndexTests
{
    private const int IndexKeyLength = sizeof(ulong) + Hash256.Size;

    internal static Hash256 Root(int id)
    {
        byte[] bytes = new byte[Hash256.Size];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Hash256.Size - sizeof(int)), id);
        return new Hash256(bytes);
    }

    private static int IndexKeyCount(IDb states) => states.GetAllKeys().Count(key => key.Length == IndexKeyLength);

    [Test]
    public void Pruning_reads_only_the_index_range_at_or_below_the_finalized_slot([Values] bool seekable)
    {
        using CountingStatesColumnsDb db = new(seekable);
        BeaconChainStore store = new(db);
        const int Below = 3;
        const int Above = 200;
        for (int i = 0; i < Below; i++)
        {
            store.PutState(Root(i), StateAt(10 + 10ul * (ulong)i));
        }

        for (int i = 0; i < Above; i++)
        {
            store.PutState(Root(1000 + i), StateAt(100 + (ulong)i));
        }

        db.Index.ResetCount();
        db.States.ResetCount();
        store.SetAnchor(Root(Below - 1), 30);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(db.Index.KeysRead, Is.LessThanOrEqualTo(Below + 1), "the entries in range, and at most the first key past it on a table that cannot seek; never one of the 200 states above");
        Assert.That(db.States.KeysRead, Is.Zero, "state manifests and chunks are never enumerated");
        Assert.That(store.TryGetState(Root(0), out _) || store.TryGetState(Root(1), out _), Is.False);
        Assert.That(store.TryGetState(Root(Below - 1), out _), Is.True, "the anchor stays");
        Assert.That(Enumerable.Range(0, Above).All(i => store.TryGetState(Root(1000 + i), out _)), Is.True);
    }

    [Test]
    public void Pruning_still_removes_the_finalized_range_after_a_restart()
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.EnsureSchemaVersion();
        store.PutState(Root(1), StateAt(10));
        store.PutState(Root(2), StateAt(40));
        store.PutState(Root(3), StateAt(90));

        BeaconChainStore restarted = new(db);
        restarted.EnsureSchemaVersion();
        restarted.SetAnchor(Root(2), 40);

        Assert.That(new[] { 1, 2, 3 }.Select(i => restarted.TryGetState(Root(i), out _)), Is.EqualTo(new[] { false, true, true }));
        Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.EqualTo(2), "a pruned state leaves no index entry");
    }

    [Test]
    public void A_state_stored_again_at_a_higher_slot_survives_the_prune_of_its_old_slot([Values] bool staleEntry)
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.PutState(Root(1), StateAt(10));
        store.PutState(Root(1), StateAt(100));
        Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.EqualTo(1), "rewriting a state removes its previous entry");
        if (staleEntry)
        {
            byte[] key = new byte[IndexKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(key, 10);
            Root(1).Bytes.CopyTo(key.AsSpan(sizeof(ulong)));
            db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex).Set(key, []);
        }

        store.PutState(Root(2), StateAt(50));

        store.SetAnchor(Root(2), 50);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.TryGetState(Root(1), out byte[]? kept), Is.True, "its slot is 100");
        Assert.That(kept, Is.EqualTo(StateAt(100)));
        Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.EqualTo(2), "the entry of its old slot is dropped; the anchor's and its own remain");
    }

    [Test]
    public void Deleting_a_state_removes_its_index_entry()
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.PutState(Root(1), StateAt(10));
        store.PutState(Root(2), StateAt(20));

        store.DeleteState(Root(1));

        Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.EqualTo(1));
    }

    [Test]
    public void State_root_and_slot_indexes_follow_state_rewrites_and_removal([Values] bool prune)
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        Hash256 blockRoot = Root(1);
        Hash256 stateRoot = Root(11);
        SignedBeaconBlock block = BeaconApiTestHost.MinimalBlock(10);
        block.Message!.StateRoot = stateRoot;
        store.PutBlock(blockRoot, block);
        store.PutState(blockRoot, StateAt(10));
        if (prune) store.SetAnchor(Root(2), 10);
        else store.DeleteState(blockRoot);

        byte[] forward = [4, .. stateRoot.Bytes.ToArray()];
        byte[] reverse = [3, .. blockRoot.Bytes.ToArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasBlock(blockRoot), Is.True);
            Assert.That(store.TryGetBlockSlot(blockRoot, out ulong blockSlot) ? blockSlot : ulong.MaxValue, Is.EqualTo(10ul));
            Assert.That(db.GetColumnDb(BeaconChainDbColumns.BlockIndex).KeyExists(forward), Is.False);
            Assert.That(db.GetColumnDb(BeaconChainDbColumns.BlockIndex).KeyExists(reverse), Is.False);
            Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.Zero);
        }

        store.PutState(blockRoot, StateAt(20));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.TryGetBlockRootByStateRoot(stateRoot, out Hash256? restored), Is.True);
        Assert.That(restored, Is.EqualTo(blockRoot));
        Assert.That(IndexKeyCount(db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex)), Is.EqualTo(1));
    }

    [Test]
    public void Pruning_includes_the_finalized_slot_at_the_uint64_boundaries([Values] bool seekable, [Values(0ul, ulong.MaxValue - 1, ulong.MaxValue)] ulong through)
    {
        using CountingStatesColumnsDb db = new(seekable);
        BeaconChainStore store = new(db);
        store.PutState(Root(1), StateAt(0));
        store.PutState(Root(2), StateAt(ulong.MaxValue - 1));
        store.PutState(Root(3), StateAt(ulong.MaxValue));

        store.SetAnchor(Root(9), through);

        Assert.That(new[] { 1, 2, 3 }.Select(i => store.TryGetState(Root(i), out _)),
            Is.EqualTo(new[] { false, through < ulong.MaxValue - 1, through < ulong.MaxValue }));
    }
}
