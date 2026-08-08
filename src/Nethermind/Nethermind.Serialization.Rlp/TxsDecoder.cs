// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;

namespace Nethermind.Serialization.Rlp;

public static class TxsDecoder
{
    private const int ParallelDecodeThreshold = 32;

    public static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors)
    {
        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        return txData.Length < ParallelDecodeThreshold
            ? DecodeSequential(txData, rlpDecoder, skipErrors)
            : DecodeParallel(txData, rlpDecoder, skipErrors);
    }

    private static Transaction DecodeTransaction(IRlpDecoder<Transaction> rlpDecoder, byte[] rlp)
    {
        RlpReader ctx = new(rlp);
        return rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
    }

    private static TransactionDecodingResult DecodeSequential(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors)
    {
        Transaction[] transactions = new Transaction[txData.Length];
        int added = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            try
            {
                transactions[added] = DecodeTransaction(rlpDecoder, txData[i]);
                added++;
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

        if (added != transactions.Length)
        {
            Array.Resize(ref transactions, added);
        }

        return new TransactionDecodingResult(transactions);
    }

#if ZK_EVM
    // Zisk stateless guest builds with --no-pthread; parallelism is unavailable, so always decode sequentially.
    private static TransactionDecodingResult DecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors) =>
        DecodeSequential(txData, rlpDecoder, skipErrors);
#else
    private static TransactionDecodingResult DecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors)
    {
        Transaction[] decoded = new Transaction[txData.Length];
        bool[] failed = new bool[1];

        ParallelUnbalancedWork.For(
            0,
            txData.Length,
            ParallelUnbalancedWork.DefaultOptions,
            (rlpDecoder, txData, decoded, failed),
            static (i, state) =>
            {
                try
                {
                    state.decoded[i] = DecodeTransaction(state.rlpDecoder, state.txData[i]);
                }
                catch
                {
                    // Any failure defers to the serial fallback, which reproduces the exact
                    // single-threaded error behavior (first invalid index, exception surface)
                    // and applies skipErrors handling.
                    Volatile.Write(ref state.failed[0], true);
                }

                return state;
            });

        return Volatile.Read(ref failed[0])
            ? DecodeSequential(txData, rlpDecoder, skipErrors)
            : new TransactionDecodingResult(decoded);
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
