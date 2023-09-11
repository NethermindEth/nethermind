// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage : ITxStorage
{
    private readonly IDb _database;

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
            Address sender = new(rlpStream.Read(20).ToArray());
            transaction = Rlp.Decode<Transaction>(rlpStream, RlpBehaviors.InMempoolForm);
            transaction.SenderAddress = sender;
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

        _database.Set(transaction.Hash,
            Bytes.Concat(
                    transaction.SenderAddress!.Bytes,
                    Rlp.Encode(transaction, RlpBehaviors.InMempoolForm).Bytes
                    ).ToArray());
    }

    public void Delete(ValueKeccak hash)
        => _database.Remove(hash.Bytes);
}
