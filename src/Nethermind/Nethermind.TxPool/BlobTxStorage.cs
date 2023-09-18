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
    private readonly IDb _database;
    private static readonly TxDecoder _txDecoder = new();

    public BlobTxStorage()
    {
        _database = new MemDb();
    }

    public BlobTxStorage(IDb database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public bool TryGet(ValueKeccak hash, [NotNullWhen(true)] out Transaction? transaction)
        => TryDecode(_database.Get(hash.Bytes), out transaction);

    private static bool TryDecode(byte[]? txBytes, out Transaction? transaction)
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

    public IEnumerable<Transaction> GetAll()
    {
        foreach (byte[] txBytes in _database.GetAllValues())
        {
            if (TryDecode(txBytes, out Transaction? transaction))
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

        _database.Set(transaction.Hash, rlpStream.Data!);
    }

    public void Delete(ValueKeccak hash)
        => _database.Remove(hash.Bytes);
}
