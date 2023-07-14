// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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

    public Transaction? Get(Keccak hash) => Decode(_database.Get(hash));

    public Transaction?[] GetAll()
    {
        byte[][] transactionsBytes = _database.GetAllValues().ToArray();
        if (transactionsBytes.Length == 0)
        {
            return Array.Empty<Transaction>();
        }

        Transaction?[] transactions = new Transaction[transactionsBytes.Length];
        for (int i = 0; i < transactionsBytes.Length; i++)
        {
            transactions[i] = Decode(transactionsBytes[i]);
        }

        return transactions;
    }

    private static Transaction? Decode(byte[]? bytes) => bytes == null ? null : Rlp.Decode<Transaction>(new Rlp(bytes));

    public void Add(Transaction transaction)
    {
        if (transaction == null || transaction.Hash == null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        _database.Set(transaction.Hash, Rlp.Encode(transaction, RlpBehaviors.None).Bytes);
    }

    public void Remove(Keccak hash) => _database.Remove(hash.Bytes);
}
