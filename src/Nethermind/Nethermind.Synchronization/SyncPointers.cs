// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization;

public class SyncPointers : ISyncPointers
{
    private readonly IDb _metadataDb;
    private readonly IDb _defaultReceiptDbColumn;

    private long? _lowestInsertedBodyNumber;
    public long? LowestInsertedBodyNumber
    {
        get => _lowestInsertedBodyNumber;
        set
        {
            _lowestInsertedBodyNumber = value;
            if (value.HasValue) _metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(value.Value).Bytes);
        }
    }

    private long? _lowestInsertedReceiptBlock;

    public long? LowestInsertedReceiptBlockNumber
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

    private long? _lowestInsertedBlockAccessListBlock;

    public long? LowestInsertedBlockAccessListBlockNumber
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
        IColumnsDb<ReceiptsColumns> receiptsDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IReceiptConfig receiptConfig)
    {
        _metadataDb = metadataDb;
        _defaultReceiptDbColumn = receiptsDb.GetColumnDb(ReceiptsColumns.Default);

        _lowestInsertedBodyNumber = ReadPointer(_metadataDb, MetadataDbKeys.LowestInsertedBodyNumber);

        byte[] lowestBytes = _defaultReceiptDbColumn.Get(Keccak.Zero);
        _lowestInsertedReceiptBlock = lowestBytes is null ? (long?)null : new Rlp.ValueDecoderContext(lowestBytes).DecodeLong();

        _lowestInsertedBlockAccessListBlock =
            ReadPointer(_metadataDb, MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber);

        // When not storing receipt, set the lowest inserted receipt to 0 so that old receipt will finish immediately
        if (!receiptConfig.StoreReceipts)
        {
            _lowestInsertedReceiptBlock = 0;
        }
    }

    private static long? ReadPointer(IDb sourceDb, int metadataKey)
    {
        byte[]? pointerBytes = sourceDb.Get(metadataKey);
        return pointerBytes is null ? null : DecodePointer(pointerBytes);
    }

    private static long DecodePointer(byte[] pointerBytes) =>
        new Rlp.ValueDecoderContext(pointerBytes).DecodeLong();
}
