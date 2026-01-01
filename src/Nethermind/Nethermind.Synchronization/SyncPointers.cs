// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
    private readonly IDb _blocksDb;
    private readonly IDb _defaultReceiptDbColumn;

    private static readonly byte[] LowestInsertedBodyNumberDbEntryAddress = ((long)0).ToBigEndianByteArrayWithoutLeadingZeros();


    private ulong? _lowestInsertedBodyNumber;
    public ulong? LowestInsertedBodyNumber
    {
        get => _lowestInsertedBodyNumber;
        set
        {
            _lowestInsertedBodyNumber = value;
            if (value.HasValue) _blocksDb[LowestInsertedBodyNumberDbEntryAddress] = Rlp.Encode(value.Value).Bytes;
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


    public SyncPointers([KeyFilter(DbNames.Blocks)] IDb blocksDb, IColumnsDb<ReceiptsColumns> receiptsDb, IReceiptConfig receiptConfig)
    {
        _blocksDb = blocksDb;
        _defaultReceiptDbColumn = receiptsDb.GetColumnDb(ReceiptsColumns.Default);

        LowestInsertedBodyNumber = _blocksDb[LowestInsertedBodyNumberDbEntryAddress]?.AsRlpValueContext().DecodeULong();

        byte[] lowestBytes = _defaultReceiptDbColumn.Get(Keccak.Zero);
        _lowestInsertedReceiptBlock = lowestBytes is null ? (ulong?)null : new RlpStream(lowestBytes).DecodeULong();

        // When not storing receipt, set the lowest inserted receipt to 0 so that old receipt will finish immediately
        if (!receiptConfig.StoreReceipts)
        {
            _lowestInsertedReceiptBlock = 0;
        }
    }
}
