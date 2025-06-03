// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public static class TxsDecoder
{
    public static TransactionDecodingResult DecodeTxs(byte[][] txData)
    {
        IRlpStreamDecoder<Transaction>? rlpDecoder = Rlp.GetStreamDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        int i = 0;
        try
        {
            Transaction[] decodedTransactions = txData.AsParallel()
				.Select(tx => Rlp.Decode(tx.AsRlpStream(), rlpDecoder, RlpBehaviors.SkipTypedWrapping))
				.AsOrdered()
				.ToArray();

            return new TransactionDecodingResult(decodedTransactions);
        }
        catch (RlpException e)
        {
            return new TransactionDecodingResult($"Transaction {i} is not valid: {e.Message}");
        }
        catch (ArgumentException)
        {
            return new TransactionDecodingResult($"Transaction {i} is not valid");
        }
    }
}

public readonly struct TransactionDecodingResult
{
    public readonly string? Error;
    public readonly Transaction[] Transactions = [];

    public TransactionDecodingResult(Transaction[] transactions)
    {
        Transactions = transactions;
    }

    public TransactionDecodingResult(string error)
    {
        Error = error;
    }
}
