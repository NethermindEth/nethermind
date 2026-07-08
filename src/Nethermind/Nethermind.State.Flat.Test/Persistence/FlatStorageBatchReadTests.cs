// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class FlatStorageBatchReadTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void TryGetStorageBatchRaw_matches_per_key_TryGetStorageRaw_for_mixed_present_and_missing()
    {
        ValueHash256 address = Hash(0x11);
        ValueHash256 fullSlot = Hash(0x21);
        ValueHash256 shortSlot = Hash(0x22);
        ValueHash256 oneByteSlot = Hash(0x23);
        ValueHash256 missingSlotA = Hash(0x24);
        ValueHash256 missingSlotB = Hash(0x25);

        byte[] full = new byte[32];
        new Random(7).NextBytes(full);
        byte[] shortValue = [0x12, 0x34, 0x56];
        byte[] oneByte = [0x7f];

        WriteRawSlots(address, [(fullSlot, full), (shortSlot, shortValue), (oneByteSlot, oneByte)]);

        ValueHash256[] slots = [fullSlot, missingSlotA, shortSlot, missingSlotB, oneByteSlot];
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        SlotValue[] expectedValues = new SlotValue[slots.Length];
        bool[] expectedFound = new bool[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            expectedFound[i] = reader.TryGetStorageRaw(address, slots[i], ref expectedValues[i]);
        }

        SlotValue[] batchValues = new SlotValue[slots.Length];
        bool[] batchFound = new bool[slots.Length];
        reader.TryGetStorageBatchRaw(address, slots, batchValues, batchFound);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Assert.That(batchFound[i], Is.EqualTo(expectedFound[i]), $"found[{i}]");
                if (expectedFound[i])
                {
                    Assert.That(batchValues[i].ToEvmBytes(), Is.EqualTo(expectedValues[i].ToEvmBytes()), $"value[{i}]");
                }
            }
        }
    }

    [Test]
    public void TryGetStorageBatchRaw_all_missing_sets_all_not_found()
    {
        ValueHash256 address = Hash(0x33);
        ValueHash256[] slots = [Hash(0x41), Hash(0x42), Hash(0x43)];

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue[] values = new SlotValue[slots.Length];
        bool[] found = new bool[slots.Length];
        reader.TryGetStorageBatchRaw(address, slots, values, found);

        Assert.That(found, Is.All.False);
    }

    [Test]
    public void TryGetStorageBatchRaw_empty_input_is_noop()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        Assert.DoesNotThrow(() => reader.TryGetStorageBatchRaw(
            Hash(0x55),
            ReadOnlySpan<ValueHash256>.Empty,
            Span<SlotValue>.Empty,
            Span<bool>.Empty));
    }

    private void WriteRawSlots(in ValueHash256 address, (ValueHash256 Slot, byte[] Value)[] entries)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.None);
        foreach ((ValueHash256 slot, byte[] value) in entries)
        {
            batch.SetStorageRawEncoded(address, slot, Rlp.Encode(((ReadOnlySpan<byte>)value).WithoutLeadingZeros()).Bytes);
        }
    }

    private static ValueHash256 Hash(byte seed)
    {
        ValueHash256 hash = ValueKeccak.Zero;
        hash.BytesAsSpan.Fill(seed);
        hash.BytesAsSpan[0] = seed;
        return hash;
    }
}
