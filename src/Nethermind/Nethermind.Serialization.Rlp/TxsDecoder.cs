// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public static class TxsDecoder
{
    private const int ParallelDecodeThreshold = 16;

    public static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors)
    {
        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        if (txData.Length >= ParallelDecodeThreshold)
        {
            return DecodeParallel(txData, rlpDecoder, skipErrors);
        }

        Transaction[] transactions = new Transaction[txData.Length];
        int added = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            try
            {
                Rlp.ValueDecoderContext ctx = new(txData[i]);
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

    private static TransactionDecodingResult DecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors)
    {
        using ArrayPoolList<Transaction?> slots = new(txData.Length, txData.Length);
        string? error = null;
        int firstError = int.MaxValue;
        object errorGate = new();

        Parallel.For(0, txData.Length, (i, state) =>
        {
            try
            {
                Rlp.ValueDecoderContext ctx = new(txData[i]);
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
            return new TransactionDecodingResult(error);
        }

        // Compact — Parallel.For doesn't preserve a contiguous prefix when skipErrors leaves nulls.
        Transaction[] result;
        if (skipErrors)
        {
            int count = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is not null) count++;
            }
            result = new Transaction[count];
            int j = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is { } tx)
                {
                    result[j++] = tx;
                }
            }
        }
        else
        {
            result = new Transaction[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                result[i] = slots[i]!;
            }
        }

        return new TransactionDecodingResult(result);
    }
}

public readonly struct TransactionDecodingResult
{
    public readonly string? Error;
    public readonly Transaction[] Transactions = [];

    public TransactionDecodingResult(Transaction[] transactions) => Transactions = transactions;

    public TransactionDecodingResult(string error) => Error = error;
}
