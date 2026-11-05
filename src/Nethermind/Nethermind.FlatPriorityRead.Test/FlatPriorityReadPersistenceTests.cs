// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using NUnit.Framework;

namespace Nethermind.FlatPriorityRead.Test;

[Parallelizable(ParallelScope.All)]
public class FlatPriorityReadPersistenceTests
{
    private static readonly Address PriorityA = TestItem.AddressA;
    private static readonly Address PriorityB = TestItem.AddressB;
    private static readonly Address Other = TestItem.AddressC;

    public static IEnumerable<MirrorCase> Changes =>
    [
        new("SetStorage", persistence => Write(persistence, batch =>
        {
            batch.SetStorage(PriorityA, 1, 7);
            batch.SetStorage(PriorityA, 2, null);
            batch.SetStorage(PriorityA, 9, UInt256.Zero);
        })),
        new("SetStorageRawEncoded", persistence => Write(persistence, batch =>
        {
            batch.SetStorageRawEncoded(PriorityA.ToAccountPath, SlotHash(1), Rlp.Encode(new byte[] { 0x2a }).Bytes);
            batch.SetStorageRawEncoded(PriorityB.ToAccountPath, SlotHash(9), Rlp.Encode(new byte[] { 0x01, 0x00 }).Bytes);
        })),
        new("SelfDestruct then revive", persistence => Write(persistence, batch =>
        {
            batch.SelfDestruct(PriorityA);
            batch.SetStorage(PriorityA, 5, 0x55);
        })),
        new("DeleteStorageRange", persistence => Write(persistence, batch =>
            batch.DeleteStorageRange(PriorityA.ToAccountPath, ValueKeccak.Zero, new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000")))),
        new("SelfDestructRange", persistence => Write(persistence, batch =>
            batch.SelfDestructRange(PriorityB.ToAccountPath, PriorityB.ToAccountPath))),
        new("Clear", persistence => persistence.Clear()),
    ];

    [Test]
    public void Serves_priority_slots_from_the_store_and_keeps_reader_snapshots([Values] FlatPriorityReadBackend backend, [Values] bool preimage)
    {
        using Fixture fixture = new(backend, preimage, batch =>
        {
            batch.SetStorage(PriorityA, 1, 1);
            batch.SetStorage(PriorityA, 2, UInt256.Zero);
            batch.SetStorage(PriorityB, 1, 0x0b);
            batch.SetStorage(Other, 1, 5);
        });

        using IPersistence.IPersistenceReader before = fixture.Priority.CreateReader();
        AssertSlots(before, (PriorityA, 1, 1), (PriorityA, 2, UInt256.Zero), (PriorityA, 3, null), (PriorityB, 1, 0x0b));
        Assert.That(fixture.Counting.SlotReads, Is.Zero, "priority reads that reached the flat persistence");

        Write(fixture.Priority, batch =>
        {
            batch.SetStorage(PriorityA, 1, 0x11);
            batch.SetStorage(PriorityA, 2, null);
            batch.SetStorage(PriorityA, 4, 0x44);
        });
        using IPersistence.IPersistenceReader after = fixture.Priority.CreateReader();

        AssertSlots(after, (PriorityA, 1, 0x11), (PriorityA, 2, null), (PriorityA, 4, 0x44), (PriorityB, 1, 0x0b), (Other, 1, 5));
        AssertSlots(before, (PriorityA, 1, 1), (PriorityA, 2, UInt256.Zero), (PriorityA, 4, null), (Other, 1, 5));
        Assert.That(fixture.Counting.SlotReads, Is.EqualTo(2), "only the reads of the other contract reach the flat persistence");
    }

    [Test]
    public void Mirrors_every_slot_change_of_the_flat_persistence([ValueSource(nameof(Changes))] MirrorCase change, [Values] FlatPriorityReadBackend backend)
    {
        using Fixture fixture = new(backend, preimage: false, batch =>
        {
            for (int slot = 1; slot <= 8; slot++)
            {
                batch.SetStorage(PriorityA, (UInt256)slot, (UInt256)(0x100 + slot));
                batch.SetStorage(PriorityB, (UInt256)slot, (UInt256)(0x200 + slot));
                batch.SetStorage(Other, (UInt256)slot, (UInt256)(0x300 + slot));
            }
        });

        change.Apply(fixture.Priority);

        using IPersistence.IPersistenceReader expected = fixture.Flat.CreateReader();
        using IPersistence.IPersistenceReader actual = fixture.Priority.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.CurrentState, Is.EqualTo(expected.CurrentState), "state");
            foreach (Address address in new[] { PriorityA, PriorityB, Other })
            {
                for (int slot = 0; slot <= 10; slot++)
                {
                    Assert.That(Read(actual, address, (UInt256)slot), Is.EqualTo(Read(expected, address, (UInt256)slot)), $"{address} slot {slot}");
                }
            }
        }
    }

    public sealed record MirrorCase(string Name, Action<IPersistence> Apply)
    {
        public override string ToString() => Name;
    }

    private static ValueHash256 SlotHash(UInt256 slot)
    {
        ValueHash256 hash = default;
        StorageTree.ComputeKeyWithLookup(slot, ref hash);
        return hash;
    }

    private static void Write(IPersistence persistence, Action<IPersistence.IWriteBatch> write)
    {
        StateId from;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader()) from = reader.CurrentState;

        ulong block = from == StateId.PreGenesis ? 0 : from.BlockNumber + 1;
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, new StateId(block, Keccak.Compute(block.ToString())));
        write(batch);
    }

    private static UInt256? Read(IPersistence.IPersistenceReader reader, Address address, UInt256 slot)
    {
        UInt256 value = default;
        return reader.TryGetSlot(address, slot, ref value) ? value : null;
    }

    private static void AssertSlots(IPersistence.IPersistenceReader reader, params (Address Address, int Slot, UInt256? Value)[] expected)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((Address address, int slot, UInt256? value) in expected)
            {
                Assert.That(Read(reader, address, (UInt256)slot), Is.EqualTo(value), $"{address} slot {slot}");
            }
        }
    }

    /// <summary>A flat persistence seeded with one batch, behind a read counter and the priority read decorator.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempPath _directory = TempPath.GetTempDirectory();
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _db = new();
        private readonly IPrioritySlotStore _store;

        public Fixture(FlatPriorityReadBackend backend, bool preimage, Action<IPersistence.IWriteBatch> seed)
        {
            Flat = preimage
                ? new PreimageRocksdbPersistence(_db, LimboLogs.Instance, FlatLayout.PreimageFlat, NullTrieNodeLog.Instance)
                : new RocksDbPersistence(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);
            Write(Flat, seed);

            Counting = new SlotReadCountingPersistence(Flat);
            _store = TestPrioritySlotStores.Create(backend, _directory.Path);
            Priority = new FlatPriorityReadPersistence(Counting, _store, new FlatPriorityReadConfig { Addresses = new() { [PriorityA.ToString()] = "A", [PriorityB.ToString()] = "B" } }, LimboLogs.Instance);
        }

        /// <summary>The flat persistence on its own, which the decorator must agree with.</summary>
        public IPersistence Flat { get; }

        public SlotReadCountingPersistence Counting { get; }

        public FlatPriorityReadPersistence Priority { get; }

        public void Dispose()
        {
            _store.Dispose();
            _db.Dispose();
            _directory.Dispose();
        }
    }
}
