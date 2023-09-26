// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage : ITxStorage
{
    private readonly IColumnsDb<BlobTxsColumns> _database;
    private readonly IDb _fullBlobTxsDb;
    private readonly IDb _lightBlobTxsDb;
    private static readonly TxDecoder _txDecoder = new();
    private static readonly LightTxDecoder _lightTxDecoder = new();

    public BlobTxStorage()
    {
        _database = new MemDbFactory().CreateColumnsDb<BlobTxsColumns>("BlobTxs");
        _fullBlobTxsDb = new MemDb();
        _lightBlobTxsDb = new MemDb();
    }

    public BlobTxStorage(IColumnsDb<BlobTxsColumns> database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _fullBlobTxsDb = database.GetColumnDb(BlobTxsColumns.FullBlobTxs);
        _lightBlobTxsDb = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
    }

    public bool TryGet(ValueKeccak hash, [NotNullWhen(true)] out Transaction? transaction)
        => TryDecodeFullTx(_fullBlobTxsDb.Get(hash.Bytes), out transaction);

    private static bool TryDecodeFullTx(byte[]? txBytes, out Transaction? transaction)
    {
        if (txBytes is not null)
        {
            RlpStream rlpStream = new(txBytes);
            Address sender = rlpStream.DecodeAddress()!;
            UInt256 timestamp = rlpStream.DecodeUInt256();
            transaction = Rlp.Decode<Transaction>(rlpStream, RlpBehaviors.InMempoolForm);
            transaction.SenderAddress = sender;
            transaction.Timestamp = timestamp;
            return true;
        }

        transaction = default;
        return false;
    }

    private static bool TryDecodeLightTx(byte[]? txBytes, out LightTransaction? lightTx)
    {
        if (txBytes is not null)
        {
            lightTx = _lightTxDecoder.Decode(txBytes);
            return true;
        }

        lightTx = default;
        return false;
    }

    public IEnumerable<LightTransaction> GetAll()
    {
        foreach (byte[] txBytes in _lightBlobTxsDb.GetAllValues())
        {
            if (TryDecodeLightTx(txBytes, out LightTransaction? transaction))
            {
                yield return transaction!;
            }
        }
    }

    public void Add(Transaction transaction)
    {
        if (transaction?.Hash is null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        int length = Rlp.LengthOf(transaction.SenderAddress);
        length += Rlp.LengthOf(transaction.Timestamp);
        length += _txDecoder.GetLength(transaction, RlpBehaviors.InMempoolForm);

        RlpStream rlpStream = new(length);
        rlpStream.Encode(transaction.SenderAddress);
        rlpStream.Encode(transaction.Timestamp);
        rlpStream.Encode(transaction, RlpBehaviors.InMempoolForm);

        _fullBlobTxsDb.Set(transaction.Hash, rlpStream.Data!);
        _lightBlobTxsDb.Set(transaction.Hash, _lightTxDecoder.Encode(transaction));
    }

    public void Delete(ValueKeccak hash)
        => _database.Remove(hash.Bytes);
}
