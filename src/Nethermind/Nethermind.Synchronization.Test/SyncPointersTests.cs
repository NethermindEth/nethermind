// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
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
    private static readonly byte[] ReceiptKey = PersistentReceiptStorage.LowestInsertedReceiptBlockKey.BytesToArray();

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

        ((TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default)).KeyWasWritten(ReceiptKey, times: 1);
    }

    [Test]
    public void WhenReceiptNotStore_WritesNonZeroReceiptPointer()
    {
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        SyncPointers pointers = CreateSyncPointers(new TestMemDb(), new ReceiptConfig()
        {
            StoreReceipts = false
        }, receiptsDb: receiptsDb);
        TestMemDb receiptColumn = (TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default);

        pointers.LowestInsertedReceiptBlockNumber = 5;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.EqualTo(5));
            receiptColumn.KeyWasWritten(ReceiptKey, times: 1);
            Assert.That(DecodePointer(receiptColumn.Get(ReceiptKey)!), Is.EqualTo(5));
        }
    }

    [Test]
    public void Writes_a_pointer_only_when_its_value_changes()
    {
        TestMemDb metadataDb = new();
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        SyncPointers pointers = CreateSyncPointers(metadataDb, receiptsDb: receiptsDb);

        foreach (ulong value in new ulong[] { 30, 30, 30, 20, 20 })
        {
            SetAll(pointers, value);
        }

        using (Assert.EnterMultipleScope())
        {
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBodyNumber), 2);
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber), 2);
            ((TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default)).KeyWasWritten(ReceiptKey, 2);
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(20));
        }
    }

    [Test]
    public void Does_not_write_a_pointer_that_returns_to_its_stored_value_after_null()
    {
        TestMemDb metadataDb = new();
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        TestMemDb receiptColumn = (TestMemDb)receiptsDb.GetColumnDb(ReceiptsColumns.Default);
        SyncPointers pointers = CreateSyncPointers(metadataDb, receiptsDb: receiptsDb);

        SetAll(pointers, 30);
        SetAll(pointers, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedBodyNumber, Is.Null);
            Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.Null);
            Assert.That(pointers.LowestInsertedBlockAccessListBlockNumber, Is.Null);
        }

        SetAll(pointers, 30);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(30));
            Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.EqualTo(30));
            Assert.That(pointers.LowestInsertedBlockAccessListBlockNumber, Is.EqualTo(30));
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBodyNumber), times: 1);
            metadataDb.KeyWasWritten(MetadataKey(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber), times: 1);
            receiptColumn.KeyWasWritten(ReceiptKey, times: 1);
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber)!), Is.EqualTo(30));
            Assert.That(DecodePointer(metadataDb.Get(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber)!), Is.EqualTo(30));
            Assert.That(DecodePointer(receiptColumn.Get(ReceiptKey)!), Is.EqualTo(30));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Pointer_matches_the_db_when_a_second_set_runs_during_the_db_write(bool blockAccessList)
    {
        InterleavingDb metadataDb = new();
        SyncPointers pointers = CreateSyncPointers(metadataDb);
        Action<ulong> set = blockAccessList
            ? value => pointers.LowestInsertedBlockAccessListBlockNumber = value
            : value => pointers.LowestInsertedBodyNumber = value;
        int key = blockAccessList ? MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber : MetadataDbKeys.LowestInsertedBodyNumber;
        Task secondSet = Task.CompletedTask;
        metadataDb.BeforeNextWrite = () =>
        {
            secondSet = Task.Run(() => set(20));
            secondSet.Wait(TimeSpan.FromMilliseconds(100));
        };

        set(30);
        await secondSet;
        set(20);

        ulong? pointer = blockAccessList ? pointers.LowestInsertedBlockAccessListBlockNumber : pointers.LowestInsertedBodyNumber;
        Assert.That(DecodePointer(metadataDb.Get(key)!), Is.EqualTo((long)pointer!.Value));
    }

    private static void SetAll(SyncPointers pointers, ulong? value)
    {
        pointers.LowestInsertedBodyNumber = value;
        pointers.LowestInsertedReceiptBlockNumber = value;
        pointers.LowestInsertedBlockAccessListBlockNumber = value;
    }

    private static SyncPointers CreateSyncPointers(IDb metadataDb, ReceiptConfig? receiptConfig = null, IDb? blocksDb = null, IColumnsDb<ReceiptsColumns>? receiptsDb = null) =>
        new(blocksDb ?? new TestMemDb(), receiptsDb ?? new TestMemColumnsDb<ReceiptsColumns>(), metadataDb, receiptConfig ?? new ReceiptConfig());

    private static byte[] MetadataKey(int key) => ((long)key).ToBigEndianByteArrayWithoutLeadingZeros();

    private static long DecodePointer(byte[] pointerBytes) =>
        RlpCodec.Read(pointerBytes, static (ref RlpReader r) => r.DecodeLong());

    private sealed class InterleavingDb : TestMemDb
    {
        public Action? BeforeNextWrite { get; set; }

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            Action? beforeWrite = BeforeNextWrite;
            BeforeNextWrite = null;
            beforeWrite?.Invoke();
            base.Set(key, value, flags);
        }
    }
}
