// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage : ITxStorage
{
    private readonly IDb _pendingTxsDb;
    private readonly IDb _processedTxsDb;
    private readonly TxDecoder _txDecoder = new();

    public BlobTxStorage(IDb pendingTxsDb, IDb processedTxsDb)
    {
        _pendingTxsDb = pendingTxsDb ?? throw new ArgumentNullException(nameof(pendingTxsDb));
        _processedTxsDb = processedTxsDb ?? throw new ArgumentNullException(nameof(processedTxsDb));
    }

    public bool TryGet(ValueKeccak hash, [NotNullWhen(true)] out Transaction? transaction)
        => TryDecode(_pendingTxsDb.Get(hash.Bytes), out transaction);

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
        foreach (byte[] txBytes in _pendingTxsDb.GetAllValues())
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

        _pendingTxsDb.Set(transaction.Hash, Rlp.Encode(transaction, RlpBehaviors.InMempoolForm).Bytes);
    }

    public void Delete(ValueKeccak hash)
        => _pendingTxsDb.Remove(hash.Bytes);

    public void AddBlobTransactionsFromBlock(long blockNumber, IList<Transaction> blockBlobTransactions)
    {
        if (blockBlobTransactions.Count == 0)
        {
            return;
        }

        int contentLength = 0;
        foreach (Transaction transaction in blockBlobTransactions)
        {
            contentLength += _txDecoder.GetLength(transaction, RlpBehaviors.InMempoolForm);
        }

        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));
        rlpStream.StartSequence(contentLength);
        foreach (Transaction transaction in blockBlobTransactions)
        {
            _txDecoder.Encode(rlpStream, transaction, RlpBehaviors.InMempoolForm);
        }

        if (rlpStream.Data is not null)
        {
            _processedTxsDb.Set(blockNumber, rlpStream.Data);
        }
    }

    public bool TryGetBlobTransactionsFromBlock(long blockNumber, out Transaction[]? blockBlobTransactions)
    {
        byte[]? bytes = _processedTxsDb.Get(blockNumber);

        if (bytes is not null)
        {
            RlpStream rlpStream = new(bytes);

            blockBlobTransactions = _txDecoder.DecodeArray(rlpStream, RlpBehaviors.InMempoolForm);
            return true;

        }

        blockBlobTransactions = default;
        return false;
    }

    public void DeleteBlobTransactionsFromBlock(long blockNumber)
        => _processedTxsDb.Delete(blockNumber);
}
