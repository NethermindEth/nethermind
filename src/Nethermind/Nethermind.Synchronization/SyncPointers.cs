// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization;

public class SyncPointers : ISyncPointers
{
    private static readonly byte[] LegacyLowestInsertedBodyNumberDbEntryAddress = ((long)0).ToBigEndianByteArrayWithoutLeadingZeros();

    private readonly PersistedPointer _lowestInsertedBody;
    private readonly PersistedPointer _lowestInsertedReceipt;
    private readonly PersistedPointer _lowestInsertedBlockAccessList;

    public ulong? LowestInsertedBodyNumber
    {
        get => _lowestInsertedBody.Value;
        set => _lowestInsertedBody.Value = value;
    }

    public ulong? LowestInsertedReceiptBlockNumber
    {
        get => _lowestInsertedReceipt.Value;
        set => _lowestInsertedReceipt.Value = value;
    }

    public ulong? LowestInsertedBlockAccessListBlockNumber
    {
        get => _lowestInsertedBlockAccessList.Value;
        set => _lowestInsertedBlockAccessList.Value = value;
    }

    public SyncPointers(
        [KeyFilter(DbNames.Blocks)] IDb blocksDb,
        IColumnsDb<ReceiptsColumns> receiptsDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IReceiptConfig receiptConfig)
    {
        _lowestInsertedBody = new PersistedPointer(metadataDb, MetadataKey(MetadataDbKeys.LowestInsertedBodyNumber));
        _lowestInsertedReceipt = new PersistedPointer(receiptsDb.GetColumnDb(ReceiptsColumns.Default), PersistentReceiptStorage.LowestInsertedReceiptBlockKey.BytesToArray());
        _lowestInsertedBlockAccessList = new PersistedPointer(metadataDb, MetadataKey(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber));

        if (_lowestInsertedBody.Value is null)
        {
            MigrateLegacyLowestInsertedBodyNumber(blocksDb);
        }

        // When not storing receipt, set the lowest inserted receipt to 0 so that old receipt will finish immediately
        if (!receiptConfig.StoreReceipts)
        {
            _lowestInsertedReceipt.SetWithoutWriting(0);
        }
    }

    private static byte[] MetadataKey(int metadataKey) => ((long)metadataKey).ToBigEndianByteArrayWithoutLeadingZeros();

    private static ulong DecodePointer(byte[] pointerBytes)
    {
        RlpReader reader = new(pointerBytes);
        return reader.DecodeULong<OffFlag>();
    }

    private void MigrateLegacyLowestInsertedBodyNumber(IDb blocksDb)
    {
        byte[]? pointerBytes = blocksDb.Get(LegacyLowestInsertedBodyNumberDbEntryAddress);
        if (pointerBytes is null)
        {
            return;
        }

        LowestInsertedBodyNumber = DecodePointer(pointerBytes);
        blocksDb.Remove(LegacyLowestInsertedBodyNumberDbEntryAddress);
    }

    private sealed class PersistedPointer
    {
        private readonly IDb _db;
        private readonly byte[] _key;
        private readonly Lock _lock = new();
        private ulong? _value;
        private ulong? _storedValue;

        public PersistedPointer(IDb db, byte[] key)
        {
            _db = db;
            _key = key;
            byte[]? pointerBytes = db.Get(key);
            _value = pointerBytes is null ? null : DecodePointer(pointerBytes);
            _storedValue = _value;
        }

        public ulong? Value
        {
            get
            {
                lock (_lock) return _value;
            }
            [SkipLocalsInit]
            set
            {
                lock (_lock)
                {
                    _value = value;
                    if (!value.HasValue || value == _storedValue) return;
                    _db.PutSpan(_key, Rlp.Encode(value.Value, stackalloc byte[Rlp.MaxLengthOfUInt64Rlp]));
                    _storedValue = value;
                }
            }
        }

        public void SetWithoutWriting(ulong value)
        {
            lock (_lock) _value = value;
        }
    }
}
