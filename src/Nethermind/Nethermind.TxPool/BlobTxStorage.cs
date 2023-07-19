// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage : ITxStorage
{
    private readonly IDb _database;

    public BlobTxStorage(IDb database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public bool TryGet(Keccak hash, out Transaction? transaction)
        => TryDecode(_database.Get(hash), out transaction);

    private static bool TryDecode(byte[]? txBytes, out Transaction? transaction)
    {
        if (txBytes is not null)
        {
            try
            {
                transaction = Rlp.Decode<Transaction>(new Rlp(txBytes), RlpBehaviors.InMempoolForm);
                return true;
            }
            catch (Exception)
            {
                // ignored
            }
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
        if (transaction == null || transaction.Hash == null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        _database.Set(transaction.Hash, Rlp.Encode(transaction, RlpBehaviors.InMempoolForm).Bytes);
    }

    public void Delete(ValueKeccak hash)
        => _database.Remove(hash.Bytes);
}
