// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SyncPointersTests
{
    private static readonly byte[] LegacyLowestInsertedBodyNumberDbEntryAddress = ((long)0).ToBigEndianByteArrayWithoutLeadingZeros();

    [Test]
    public void Stores_body_and_block_access_list_pointers_in_metadata_db()
    {
        IDb metadataDb = new TestMemDb();
        SyncPointers pointers = CreateSyncPointers(metadataDb);

        pointers.LowestInsertedBodyNumber = 11;
        pointers.LowestInsertedBlockAccessListBlockNumber = 22;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(11));
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber)!), Is.EqualTo(22));
        }
    }

    [Test]
    public void Reads_body_and_block_access_list_pointers_from_metadata_db()
    {
        IDb metadataDb = new TestMemDb();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(33));
        metadataDb.Set(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber, Rlp.Encode(44));

        SyncPointers pointers = CreateSyncPointers(metadataDb);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(33));
            Assert.That(pointers.LowestInsertedBlockAccessListBlockNumber, Is.EqualTo(44));
        }
    }

    [Test]
    public void Migrates_legacy_body_pointer_from_blocks_db_to_metadata_db()
    {
        IDb metadataDb = new TestMemDb();
        IDb blocksDb = new TestMemDb();
        blocksDb.Set(LegacyLowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(55));

        SyncPointers pointers = CreateSyncPointers(metadataDb, blocksDb: blocksDb);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(55));
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(55));
            Assert.That(blocksDb.Get(LegacyLowestInsertedBodyNumberDbEntryAddress), Is.Null);
        }
    }

    [Test]
    public void Metadata_body_pointer_takes_precedence_over_legacy_blocks_db_pointer()
    {
        IDb metadataDb = new TestMemDb();
        IDb blocksDb = new TestMemDb();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(66));
        blocksDb.Set(LegacyLowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(77));

        SyncPointers pointers = CreateSyncPointers(metadataDb, blocksDb: blocksDb);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(66));
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(66));
            Assert.That(DecodePointer(blocksDb.Get(LegacyLowestInsertedBodyNumberDbEntryAddress)!), Is.EqualTo(77));
        }
    }

    [Test]
    public void WhenReceiptNotStore_SetLowestInsertedReceiptTo0()
    {
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        SyncPointers pointers = CreateSyncPointers(new TestMemDb(), new ReceiptConfig()
        {
            StoreReceipts = false
        }, receiptsDb: receiptsDb);

        Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.EqualTo(0));

        pointers.LowestInsertedReceiptBlockNumber = 0;

        ((TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default)).KeyWasWritten(PersistentReceiptStorage.LowestInsertedReceiptBlockKey.BytesToArray());
    }

    [Test]
    public void Writes_a_pointer_only_when_its_value_changes()
    {
        TestMemDb metadataDb = new();
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        SyncPointers pointers = CreateSyncPointers(metadataDb, receiptsDb: receiptsDb);

        foreach (ulong value in new ulong[] { 30, 30, 30, 20, 20 })
        {
            pointers.LowestInsertedBodyNumber = value;
            pointers.LowestInsertedReceiptBlockNumber = value;
            pointers.LowestInsertedBlockAccessListBlockNumber = value;
        }

        using (Assert.EnterMultipleScope())
        {
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBodyNumber), 2);
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber), 2);
            ((TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default)).KeyWasWritten(PersistentReceiptStorage.LowestInsertedReceiptBlockKey.BytesToArray(), 2);
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(20));
        }
    }

    private static SyncPointers CreateSyncPointers(IDb metadataDb, ReceiptConfig? receiptConfig = null, IDb? blocksDb = null, IColumnsDb<ReceiptsColumns>? receiptsDb = null) =>
        new(blocksDb ?? new TestMemDb(), receiptsDb ?? new TestMemColumnsDb<ReceiptsColumns>(), metadataDb, receiptConfig ?? new ReceiptConfig());

    private static byte[] MetadataKey(int key) => ((long)key).ToBigEndianByteArrayWithoutLeadingZeros();

    private static long DecodePointer(byte[] pointerBytes) =>
        RlpCodec.Read(pointerBytes, static (ref RlpReader r) => r.DecodeLong());
}
