// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization;

public class SyncPointers : ISyncPointers
{
    private readonly IDb _metadataDb;
    private readonly IDb _defaultReceiptDbColumn;

    private static readonly byte[] LegacyLowestInsertedBodyNumberDbEntryAddress = ((long)0).ToBigEndianByteArrayWithoutLeadingZeros();

    private ulong? _lowestInsertedBodyNumber;
    public ulong? LowestInsertedBodyNumber
    {
        get => _lowestInsertedBodyNumber;
        set
        {
            _lowestInsertedBodyNumber = value;
            if (value.HasValue) _metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(value.Value).Bytes);
        }
    }

    private ulong? _lowestInsertedReceiptBlock;

    public ulong? LowestInsertedReceiptBlockNumber
    {
        get => _lowestInsertedReceiptBlock;
        set
        {
            _lowestInsertedReceiptBlock = value;
            if (value.HasValue)
            {
                _defaultReceiptDbColumn.Set(Keccak.Zero, Rlp.Encode(value.Value).Bytes);
            }
        }
    }

    private ulong? _lowestInsertedBlockAccessListBlock;

    public ulong? LowestInsertedBlockAccessListBlockNumber
    {
        get => _lowestInsertedBlockAccessListBlock;
        set
        {
            _lowestInsertedBlockAccessListBlock = value;
            if (value.HasValue)
            {
                _metadataDb.Set(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber, Rlp.Encode(value.Value).Bytes);
            }
        }
    }


    public SyncPointers(
        [KeyFilter(DbNames.Blocks)] IDb blocksDb,
        IColumnsDb<ReceiptsColumns> receiptsDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IReceiptConfig receiptConfig)
    {
        _metadataDb = metadataDb;
        _defaultReceiptDbColumn = receiptsDb.GetColumnDb(ReceiptsColumns.Default);

        _lowestInsertedBodyNumber = ReadPointer(_metadataDb, MetadataDbKeys.LowestInsertedBodyNumber);
        if (_lowestInsertedBodyNumber is null)
        {
            MigrateLegacyLowestInsertedBodyNumber(blocksDb);
        }

        byte[] lowestBytes = _defaultReceiptDbColumn.Get(Keccak.Zero);
        _lowestInsertedReceiptBlock = lowestBytes is null ? (ulong?)null : new RlpReader(lowestBytes).DecodeULong();

        _lowestInsertedBlockAccessListBlock =
            ReadPointer(_metadataDb, MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber);

        // When not storing receipt, set the lowest inserted receipt to 0 so that old receipt will finish immediately
        if (!receiptConfig.StoreReceipts)
        {
            _lowestInsertedReceiptBlock = 0;
        }
    }

    private static ulong? ReadPointer(IDb sourceDb, int metadataKey)
    {
        byte[]? pointerBytes = sourceDb.Get(metadataKey);
        return pointerBytes is null ? null : DecodePointer(pointerBytes);
    }

    private static ulong DecodePointer(byte[] pointerBytes) =>
        new RlpReader(pointerBytes).DecodeULong();

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
}
