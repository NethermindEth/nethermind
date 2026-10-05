// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Storage;

// fulu/p2p-interface.md DataColumnSidecarsByRange: the start-up check decides which stored slots can be served complete.
public class BeaconChainStoreDataColumnScanTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ulong FuluStart = Spec.FuluForkEpoch * Spec.SlotsPerEpoch;
    private static readonly UInt128 Required = UInt128.One << 3 | UInt128.One << 70;

    private static Hash256 RootAt(ulong slot) => Keccak.Compute($"canonical {slot}");

    [Test]
    public void A_read_fault_fails_the_slot_it_hits_and_none_above()
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        ulong top = FuluStart + 100;
        store.ApplyCanonicalIndexChanges([(top, RootAt(top))], top);
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(3, top, RootAt(top)));
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(70, top, RootAt(top)));
        Assert.That(store.FindIncompleteDataColumnSlot(FuluStart, top, null, Required, out _), Is.Null, "complete before the fault");

        db.FailReads = true;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.FindIncompleteDataColumnSlot(FuluStart, top, null, Required, out BeaconChainStore.DataColumnShortfall shortfall), Is.EqualTo(top));
        Assert.That(shortfall, Is.EqualTo(BeaconChainStore.DataColumnShortfall.ReadFailed));
    }

    [Test]
    public void Blocks_before_Fulu_need_no_columns()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        ulong slot = FuluStart - 1;
        SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
        block.Message!.Body!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        store.PutBlock(RootAt(slot), block);
        store.ApplyCanonicalIndexChanges([(slot, RootAt(slot))], slot);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.FindIncompleteDataColumnSlot(0, slot, null, Required, out _), Is.Null, "a Deneb/Electra block's blobs are blob sidecars, not columns");
        Assert.That(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()).FindIncompleteDataColumnSlot(0, 0, null, Required, out _), Is.Null, "an empty store");
    }

    [Test]
    public void A_Gloas_block_needs_its_columns_only_once_its_payload_envelope_is_stored([Values] bool envelopeStored)
    {
        const ulong gloasEpoch = 500_000;
        BeaconChainSpec spec = Spec.WithGloasForkOverride(gloasEpoch, "0x07000000");
        MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, spec);
        ulong slot = gloasEpoch * spec.SlotsPerEpoch + 5;
        store.ApplyCanonicalIndexChanges([(slot, RootAt(slot))], slot);
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(3, slot, RootAt(slot)));
        if (envelopeStored)
        {
            db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes).Set(RootAt(slot).Bytes, [1]);
        }

        Assert.That(store.FindIncompleteDataColumnSlot(0, slot, null, Required, out _), Is.EqualTo(envelopeStored ? slot : (ulong?)null),
            "column 70 is missing; a withheld payload never needed it");
    }

    [Test]
    [Explicit("Measures start-up cost; run by name")]
    public void Scan_time_over_a_full_mainnet_window()
    {
        const int columnsPerBlock = 8;
        ulong slots = Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests * Spec.SlotsPerEpoch;
        ulong first = FuluStart + 1_000_000;
        ulong last = first + slots - 1;
        MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, Spec);
        IDb table = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
        UInt128 required = UInt128.Zero;
        for (int i = 0; i < columnsPerBlock; i++)
        {
            required |= UInt128.One << (i * 16 + 5);
        }

        List<(ulong Slot, Hash256? Root)> canonical = new((int)slots);
        byte[] slotKey = new byte[sizeof(ulong)];
        byte[] recordKey = new byte[Hash256.Size + sizeof(ulong)];
        byte[] record = new byte[9];
        for (ulong slot = first; slot <= last; slot++)
        {
            Hash256 root = RootAt(slot);
            canonical.Add((slot, root));
            byte[] entry = new byte[Hash256.Size + 16];
            root.Bytes.CopyTo(entry);
            BinaryPrimitives.WriteUInt128BigEndian(entry.AsSpan(Hash256.Size), required);
            BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
            table.Set(slotKey, entry);
            root.Bytes.CopyTo(recordKey);
            for (int i = 0; i < columnsPerBlock; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(recordKey.AsSpan(Hash256.Size), (ulong)(i * 16 + 5));
                table.Set(recordKey, record);
            }
        }

        store.ApplyCanonicalIndexChanges(canonical, last);

        Stopwatch timer = Stopwatch.StartNew();
        ulong? incomplete = store.FindIncompleteDataColumnSlot(first, last, null, required, out _);
        timer.Stop();

        TestContext.Out.WriteLine($"Scanned {slots} slots, {slots * columnsPerBlock} records, in {timer.Elapsed.TotalMilliseconds:F0} ms (in-memory db)");
        Assert.That(incomplete, Is.Null);
    }

    [Test]
    [Explicit("Measures start-up cost; run by name")]
    public void Block_read_time_for_canonical_slots_without_columns()
    {
        const int blocks = 1024;
        ulong first = FuluStart + 1_000_000;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        Random random = new(42);
        List<(ulong Slot, Hash256? Root)> canonical = new(blocks);
        for (ulong slot = first; slot < first + blocks; slot++)
        {
            SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
            Transaction[] transactions = new Transaction[500];
            for (int i = 0; i < transactions.Length; i++)
            {
                byte[] bytes = new byte[200];
                random.NextBytes(bytes);
                transactions[i] = new Transaction { Bytes = bytes };
            }

            block.Message!.Body!.ExecutionPayload!.Transactions = transactions;
            store.PutBlock(RootAt(slot), block);
            canonical.Add((slot, RootAt(slot)));
        }

        store.ApplyCanonicalIndexChanges(canonical, first + blocks - 1);

        Stopwatch timer = Stopwatch.StartNew();
        ulong? incomplete = store.FindIncompleteDataColumnSlot(first, first + blocks - 1, null, Required, out _);
        timer.Stop();

        TestContext.Out.WriteLine($"Read {blocks} blob-less blocks in {timer.Elapsed.TotalMilliseconds:F0} ms, {timer.Elapsed.TotalMicroseconds / blocks:F0} us each (in-memory db)");
        Assert.That(incomplete, Is.Null);
    }
}
