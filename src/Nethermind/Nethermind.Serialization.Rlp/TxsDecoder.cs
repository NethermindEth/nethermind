// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public static class TxsDecoder
{
    public static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors)
    {
        IRlpStreamDecoder<Transaction>? rlpDecoder = Rlp.GetStreamDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        var transactions = new Transaction[txData.Length];

        int added = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            try
            {
                transactions[added++] = Rlp.Decode(txData[i].AsRlpStream(), rlpDecoder, RlpBehaviors.SkipTypedWrapping);
            }
            catch (RlpException e)
            {
                if (skipErrors)
                {
                    continue;
                }

                return new TransactionDecodingResult($"Transaction {i} is not valid: {e.Message}");
            }
            catch (ArgumentException)
            {
                if (skipErrors)
                {
                    continue;
                }

                return new TransactionDecodingResult($"Transaction {i} is not valid");
            }
        }

        return new TransactionDecodingResult(transactions);
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
