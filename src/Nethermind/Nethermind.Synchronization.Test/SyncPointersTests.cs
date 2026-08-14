// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
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

        Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(11));
        Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber)!), Is.EqualTo(22));
    }

    [Test]
    public void Reads_body_and_block_access_list_pointers_from_metadata_db()
    {
        IDb metadataDb = new TestMemDb();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(33).Bytes);
        metadataDb.Set(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber, Rlp.Encode(44).Bytes);

        SyncPointers pointers = CreateSyncPointers(metadataDb);

        Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(33));
        Assert.That(pointers.LowestInsertedBlockAccessListBlockNumber, Is.EqualTo(44));
    }

    [Test]
    public void Migrates_legacy_body_pointer_from_blocks_db_to_metadata_db()
    {
        IDb metadataDb = new TestMemDb();
        IDb blocksDb = new TestMemDb();
        blocksDb.Set(LegacyLowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(55).Bytes);

        SyncPointers pointers = CreateSyncPointers(metadataDb, blocksDb: blocksDb);

        Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(55));
        Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(55));
        Assert.That(blocksDb.Get(LegacyLowestInsertedBodyNumberDbEntryAddress), Is.Null);
    }

    [Test]
    public void Metadata_body_pointer_takes_precedence_over_legacy_blocks_db_pointer()
    {
        IDb metadataDb = new TestMemDb();
        IDb blocksDb = new TestMemDb();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(66).Bytes);
        blocksDb.Set(LegacyLowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(77).Bytes);

        SyncPointers pointers = CreateSyncPointers(metadataDb, blocksDb: blocksDb);

        Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(66));
        Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(66));
        Assert.That(DecodePointer(blocksDb.Get(LegacyLowestInsertedBodyNumberDbEntryAddress)!), Is.EqualTo(77));
    }

    [Test]
    public void WhenReceiptNotStore_SetLowestInsertedReceiptTo0()
    {
        SyncPointers pointers = CreateSyncPointers(new TestMemDb(), new ReceiptConfig()
        {
            StoreReceipts = false
        });

        Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.EqualTo(0));
    }

    private static SyncPointers CreateSyncPointers(IDb metadataDb, ReceiptConfig? receiptConfig = null, IDb? blocksDb = null) =>
        new(blocksDb ?? new TestMemDb(), new TestMemColumnsDb<ReceiptsColumns>(), metadataDb, receiptConfig ?? new ReceiptConfig());

    private static long DecodePointer(byte[] pointerBytes) =>
        new RlpReader(pointerBytes).DecodeLong();
}
