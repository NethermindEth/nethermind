// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public static class TxsDecoder
{
    public static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors)
    {
        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        return TryDecodeParallel(txData, rlpDecoder, skipErrors, out TransactionDecodingResult result)
            ? result
            : DecodeSequential(txData, rlpDecoder, skipErrors);
    }

    private static TransactionDecodingResult DecodeSequential(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors)
    {
        Transaction[] transactions = new Transaction[txData.Length];
        int added = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            try
            {
                RlpReader ctx = new(txData[i]);
                Transaction decoded = rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
                transactions[added++] = decoded;
            }
            catch (RlpException e)
            {
                if (skipErrors) continue;
                return new TransactionDecodingResult($"Transaction {i} is not valid: {e.Message}");
            }
            catch (ArgumentException)
            {
                if (skipErrors) continue;
                return new TransactionDecodingResult($"Transaction {i} is not valid");
            }
        }

        if (skipErrors && added != transactions.Length)
        {
            Array.Resize(ref transactions, added);
        }

        return new TransactionDecodingResult(transactions);
    }

#if !ZK_EVM
    private const int ParallelDecodeThreshold = 16;

    private static bool TryDecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors, out TransactionDecodingResult result)
    {
        if (txData.Length < ParallelDecodeThreshold)
        {
            result = default;
            return false;
        }

        Transaction[] slots = new Transaction[txData.Length];
        string? error = null;
        int firstError = int.MaxValue;
        object errorGate = new();

        Parallel.For(0, txData.Length, (i, state) =>
        {
            try
            {
                RlpReader ctx = new(txData[i]);
                slots[i] = rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
            }
            catch (Exception e) when (e is RlpException or ArgumentException)
            {
                if (skipErrors) return;
                lock (errorGate)
                {
                    if (i < firstError)
                    {
                        firstError = i;
                        error = e is RlpException rlpEx
                            ? $"Transaction {i} is not valid: {rlpEx.Message}"
                            : $"Transaction {i} is not valid";
                    }
                }
                state.Stop();
            }
        });

        if (error is not null)
        {
            result = new TransactionDecodingResult(error);
            return true;
        }

        if (!skipErrors)
        {
            result = new TransactionDecodingResult(slots);
            return true;
        }

        int j = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not null)
            {
                if (i != j) slots[j] = slots[i];
                j++;
            }
        }
        if (j != slots.Length) Array.Resize(ref slots, j);
        result = new TransactionDecodingResult(slots);
        return true;
    }
#else
    // Zisk stateless guest builds with --no-pthread; BCL Parallel.For is unavailable, so the
    // dispatch always short-circuits to DecodeSequential.
    private static bool TryDecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors, out TransactionDecodingResult result)
    {
        result = default;
        return false;
    }
#endif
}

public readonly struct TransactionDecodingResult
{
    public readonly string? Error;
    public readonly Transaction[] Transactions = [];

    public TransactionDecodingResult(Transaction[] transactions) => Transactions = transactions;

    public TransactionDecodingResult(string error) => Error = error;
}
