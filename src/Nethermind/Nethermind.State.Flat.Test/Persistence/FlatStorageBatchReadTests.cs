// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

/// <summary>
/// The TrieWarmer coalesces same-contract slot reads via <see cref="IPersistence.IPersistenceReader.TryGetStorageBatchRaw" />.
/// These tests pin that the batched read returns byte-for-byte the same values (and found flags) as the
/// per-key <see cref="IPersistence.IPersistenceReader.TryGetStorageRaw" /> path, including missing keys
/// and leading-zero-trimmed values.
/// </summary>
[TestFixture]
public class FlatStorageBatchReadTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    private static ValueHash256 Addr(byte seed)
    {
        ValueHash256 h = ValueKeccak.Zero;
        h.BytesAsSpan.Fill(seed);
        return h;
    }

    private static ValueHash256 Slot(byte seed)
    {
        ValueHash256 h = ValueKeccak.Zero;
        h.BytesAsSpan.Fill((byte)(seed ^ 0xA5));
        h.BytesAsSpan[0] = seed; // keep slots distinct
        return h;
    }

    private void WriteSlots(in ValueHash256 addr, (ValueHash256 slot, byte[] value)[] entries)
    {
        StateId from;
        using (IPersistence.IPersistenceReader r = _persistence.CreateReader())
        {
            from = r.CurrentState;
        }

        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(from, new StateId(from.BlockNumber + 1, Keccak.EmptyTreeHash), WriteFlags.None);
        foreach ((ValueHash256 slot, byte[] value) in entries)
        {
            batch.SetStorageRaw(addr, slot, new SlotValue(value));
        }
    }

    [Test]
    public void TryGetStorageBatchRaw_matches_per_key_TryGetStorageRaw_for_mixed_present_and_missing()
    {
        ValueHash256 addr = Addr(0x11);

        // A 32-byte value, a leading-zero-trimmed short value, and a single byte — exercise all decode lengths.
        byte[] full = new byte[32];
        new Random(7).NextBytes(full);
        byte[] shortVal = [0x12, 0x34, 0x56];
        byte[] oneByte = [0x7f];

        ValueHash256 sFull = Slot(1);
        ValueHash256 sShort = Slot(2);
        ValueHash256 sOne = Slot(3);
        ValueHash256 sMissingA = Slot(4);
        ValueHash256 sMissingB = Slot(5);

        WriteSlots(addr, [(sFull, full), (sShort, shortVal), (sOne, oneByte)]);

        ValueHash256[] slots = [sFull, sMissingA, sShort, sMissingB, sOne];

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Per-key reference.
        SlotValue[] expectedValues = new SlotValue[slots.Length];
        bool[] expectedFound = new bool[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            expectedFound[i] = reader.TryGetStorageRaw(addr, slots[i], ref expectedValues[i]);
        }

        // Batched.
        SlotValue[] batchValues = new SlotValue[slots.Length];
        bool[] batchFound = new bool[slots.Length];
        reader.TryGetStorageBatchRaw(addr, slots, batchValues, batchFound);

        for (int i = 0; i < slots.Length; i++)
        {
            Assert.That(batchFound[i], Is.EqualTo(expectedFound[i]), $"found flag mismatch at {i}");
            if (expectedFound[i])
            {
                Assert.That(batchValues[i].AsReadOnlySpan.ToArray(), Is.EqualTo(expectedValues[i].AsReadOnlySpan.ToArray()), $"value mismatch at {i}");
            }
        }
    }

    [Test]
    public void TryGetStorageBatchRaw_all_missing_sets_all_not_found()
    {
        ValueHash256 addr = Addr(0x22);
        ValueHash256[] slots = [Slot(10), Slot(11), Slot(12)];

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        SlotValue[] values = new SlotValue[slots.Length];
        bool[] found = new bool[slots.Length];
        reader.TryGetStorageBatchRaw(addr, slots, values, found);

        Assert.That(found, Is.All.False);
    }

    [Test]
    public void TryGetStorageBatchRaw_empty_input_is_noop()
    {
        ValueHash256 addr = Addr(0x33);
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.DoesNotThrow(() => reader.TryGetStorageBatchRaw(addr, ReadOnlySpan<ValueHash256>.Empty, Span<SlotValue>.Empty, Span<bool>.Empty));
    }
}
