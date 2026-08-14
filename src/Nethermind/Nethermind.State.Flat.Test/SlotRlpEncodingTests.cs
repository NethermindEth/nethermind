// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SlotRlpEncodingTests
{
    private static readonly Address Addr = TestItem.AddressA;
    private static readonly UInt256 Slot = 7;

    private static readonly byte[] LayoutKey = Keccak.Compute("Layout").BytesToArray();
    private static readonly byte[] SlotEncodingKey = Keccak.Compute("SlotEncoding").BytesToArray();

    private static RocksDbPersistence CreatePersistence(IColumnsDb<FlatDbColumns> db, bool rlpWrap = true)
    {
        // Raw mode only exists for DBs synced before the feature; pin it to exercise the legacy path.
        if (!rlpWrap) BasePersistence.SetSlotEncoding(db.GetColumnDb(FlatDbColumns.Metadata), BasePersistence.SlotEncodingRaw);
        return new RocksDbPersistence(db, LimboLogs.Instance);
    }

    private static void WriteSlot(IPersistence persistence, in SlotValue value)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        batch.SetStorage(Addr, Slot, value);
    }

    private static void WriteSlotEncoded(IPersistence persistence, byte[] rlpValue)
    {
        ValueHash256 addrHash = ValueKeccak.Compute(Addr.Bytes);
        Span<byte> slotBytes = stackalloc byte[32];
        Slot.ToBigEndian(slotBytes);
        ValueHash256 slotHash = ValueKeccak.Compute(slotBytes);

        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        batch.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
    }

    private static void WriteRawSlotToDb(IColumnsDb<FlatDbColumns> db, byte[] strippedValue)
    {
        IDb storageDb = db.GetColumnDb(FlatDbColumns.Storage);
        ValueHash256 addrHash = ValueKeccak.Compute(Addr.Bytes);
        Span<byte> slotBytes = stackalloc byte[32];
        Slot.ToBigEndian(slotBytes);
        ValueHash256 slotHash = ValueKeccak.Compute(slotBytes);

        byte[] storageKey = new byte[52];
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan()[..4]);
        slotHash.Bytes.CopyTo(storageKey.AsSpan()[4..36]);
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan()[36..52]);
        storageDb.Set(storageKey, strippedValue);
    }

    // Stripped (significant) bytes covering: single byte < 0x80, single byte >= 0x80, two bytes, full 32 bytes.
    [TestCase(true, "05")]
    [TestCase(false, "05")]
    [TestCase(true, "80")]
    [TestCase(false, "80")]
    [TestCase(true, "ff")]
    [TestCase(false, "ff")]
    [TestCase(true, "0102")]
    [TestCase(false, "0102")]
    [TestCase(true, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [TestCase(false, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void Slot_value_round_trips(bool rlpWrap, string strippedHex)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = CreatePersistence(db, rlpWrap);
        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString(strippedHex));

        WriteSlot(persistence, value);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue read = default;
        Assert.That(reader.TryGetSlot(Addr, Slot, ref read), Is.True);
        Assert.That(read.AsReadOnlySpan.ToArray(), Is.EqualTo(value.AsReadOnlySpan.ToArray()));
    }

    // The sync path feeds the trie-leaf RLP value (RLP(stripped)) directly via SetStorageRawEncoded.
    // It is only valid with wrapping on, where the leaf bytes are stored verbatim (no decode + re-encode).
    [TestCase("05")]
    [TestCase("0102")]
    [TestCase("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void SetStorageRawEncoded_round_trips_and_stores_verbatim_when_wrapping(string strippedHex)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = CreatePersistence(db, rlpWrap: true);

        byte[] stripped = Bytes.FromHexString(strippedHex);
        byte[] rlpLeaf = Rlp.Encode((ReadOnlySpan<byte>)stripped).Bytes; // trie leaf value == RLP(stripped)
        WriteSlotEncoded(persistence, rlpLeaf);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue read = default;
        Assert.That(reader.TryGetSlot(Addr, Slot, ref read), Is.True);
        Assert.That(read.ToEvmBytes(), Is.EqualTo(stripped));

        // The leaf RLP is stored verbatim — byte-identical to our on-disk format.
        Assert.That(ReadStoredSlotBytes(db), Is.EqualTo(rlpLeaf));
    }

    [Test]
    public void SetStorageRawEncoded_is_unsupported_in_raw_mode()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = CreatePersistence(db, rlpWrap: false);

        byte[] rlpLeaf = Rlp.Encode((ReadOnlySpan<byte>)Bytes.FromHexString("0102")).Bytes;
        Assert.That(() => WriteSlotEncoded(persistence, rlpLeaf), Throws.InstanceOf<NotSupportedException>());
    }

    [Test]
    public void Fresh_db_defaults_to_rlp_and_records_version()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = CreatePersistence(db, rlpWrap: true);

        WriteSlot(persistence, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0102")));

        Assert.That(db.GetColumnDb(FlatDbColumns.Metadata).Get(SlotEncodingKey),
            Is.EqualTo(new[] { BasePersistence.SlotEncodingRlp }));

        // The stored blob is RLP(0x0102) = 0x82 0x01 0x02, not the raw stripped bytes.
        Assert.That(ReadStoredSlotBytes(db), Is.EqualTo(Bytes.FromHexString("820102")));
    }

    // Regression: a pre-feature DB holds raw slots but may lack BOTH metadata markers, so detection must key
    // on slot presence — otherwise the raw values are misread as RLP and crash on the first SLOAD.
    [TestCase(true)]
    [TestCase(false)]
    public void Pre_feature_db_with_raw_slots_falls_back_to_raw(bool seedLayoutMarker)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        if (seedLayoutMarker) db.GetColumnDb(FlatDbColumns.Metadata).Set(LayoutKey, new[] { (byte)FlatLayout.Flat });
        WriteRawSlotToDb(db, Bytes.FromHexString("0102"));

        RocksDbPersistence persistence = new(db, LimboLogs.Instance); // no recorded SlotEncoding; raw slots win
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            SlotValue read = default;
            Assert.That(reader.TryGetSlot(Addr, Slot, ref read), Is.True);
            Assert.That(read.ToEvmBytes(), Is.EqualTo(Bytes.FromHexString("0102")));
        }

        // Writes on the legacy DB stay raw and never stamp the metadata markers.
        WriteSlot(persistence, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("abcd")));
        Assert.That(db.GetColumnDb(FlatDbColumns.Metadata).Get(SlotEncodingKey), Is.Null);
        Assert.That(ReadStoredSlotBytes(db), Is.EqualTo(Bytes.FromHexString("abcd"))); // raw, not RLP(0x82abcd)

        RocksDbPersistence reopened = new(db, LimboLogs.Instance);
        using IPersistence.IPersistenceReader reader2 = reopened.CreateReader();
        SlotValue read2 = default;
        Assert.That(reader2.TryGetSlot(Addr, Slot, ref read2), Is.True);
        Assert.That(read2.ToEvmBytes(), Is.EqualTo(Bytes.FromHexString("abcd")));
    }

    // A Layout marker without slots (e.g. accounts synced but no storage yet) is still a brand-new DB — it wraps.
    [Test]
    public void Empty_db_with_layout_marker_defaults_to_rlp()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        db.GetColumnDb(FlatDbColumns.Metadata).Set(LayoutKey, new[] { (byte)FlatLayout.Flat });

        RocksDbPersistence persistence = new(db, LimboLogs.Instance);
        WriteSlot(persistence, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0102")));

        Assert.That(db.GetColumnDb(FlatDbColumns.Metadata).Get(SlotEncodingKey),
            Is.EqualTo(new[] { BasePersistence.SlotEncodingRlp }));
        Assert.That(ReadStoredSlotBytes(db), Is.EqualTo(Bytes.FromHexString("820102")));
    }

    [Test]
    public void Unknown_slot_encoding_version_throws()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        db.GetColumnDb(FlatDbColumns.Metadata).Set(SlotEncodingKey, new byte[] { 2 });

        Assert.That(() => CreatePersistence(db), Throws.TypeOf<InvalidConfigurationException>());
    }

    private static byte[] ReadStoredSlotBytes(IColumnsDb<FlatDbColumns> db)
    {
        ValueHash256 addrHash = ValueKeccak.Compute(Addr.Bytes);
        Span<byte> slotBytes = stackalloc byte[32];
        Slot.ToBigEndian(slotBytes);
        ValueHash256 slotHash = ValueKeccak.Compute(slotBytes);

        byte[] storageKey = new byte[52];
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan()[..4]);
        slotHash.Bytes.CopyTo(storageKey.AsSpan()[4..36]);
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan()[36..52]);
        return db.GetColumnDb(FlatDbColumns.Storage).Get(storageKey)!;
    }
}
