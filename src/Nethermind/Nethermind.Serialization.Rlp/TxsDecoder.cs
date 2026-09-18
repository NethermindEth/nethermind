// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes a list of EIP-2718 <c>TransactionType || TransactionPayload</c> entries.</summary>
public static partial class TxsDecoder
{
    private const int ParallelDecodeThreshold = 32;

    /// <param name="skipErrors">
    /// When set, undecodable entries are dropped and the result is compacted, so the returned array can be
    /// shorter than <paramref name="txData"/> and never carries an error. Otherwise the first bad entry
    /// fails the whole call.
    /// </param>
    /// <remarks>Copies calldata and delayed-hash bytes so decoded transactions do not borrow the input.
    /// Long lists decode in parallel and fall back to the serial pass on any failure, so the
    /// reported error is always the one a single-threaded decode would have produced.</remarks>
    public static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors) => DecodeTxs(txData, skipErrors, borrowMemory: false);

    /// <summary>Decodes transactions borrowing the input buffers.</summary>
    /// <remarks>Calldata and delayed-hash bytes alias the caller's arrays, which must remain unmodified
    /// for the lifetime of the decoded transactions. Use <see cref="DecodeTxs(byte[][], bool)"/> when copying is required.</remarks>
    internal static TransactionDecodingResult DecodeTxsBorrowingBuffers(byte[][] txData, bool skipErrors) => DecodeTxs(txData, skipErrors, borrowMemory: true);

    private static TransactionDecodingResult DecodeTxs(byte[][] txData, bool skipErrors, bool borrowMemory)
    {
        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null) return new TransactionDecodingResult($"{nameof(Transaction)} decoder is not registered");

        return txData.Length < ParallelDecodeThreshold
            ? DecodeSequential(txData, rlpDecoder, skipErrors, borrowMemory)
            : DecodeParallel(txData, rlpDecoder, skipErrors, borrowMemory);
    }

    private static Transaction DecodeTransaction(IRlpDecoder<Transaction> rlpDecoder, byte[] rlp, bool borrowMemory)
    {
        RlpReader ctx = borrowMemory ? new(rlp.AsMemory()) : new(rlp);
        return rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
    }

    private static TransactionDecodingResult DecodeSequential(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors, bool borrowMemory)
    {
        Transaction[] transactions = new Transaction[txData.Length];
        int added = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            try
            {
                transactions[added] = DecodeTransaction(rlpDecoder, txData[i], borrowMemory);
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
}

/// <summary>Outcome of <see cref="TxsDecoder.DecodeTxs"/>: <see cref="Error"/> is non-null exactly when
/// decoding failed, and <see cref="Transactions"/> is empty in that case.</summary>
public readonly struct TransactionDecodingResult
{
    public readonly string? Error;
    public readonly Transaction[] Transactions = [];

    public TransactionDecodingResult(Transaction[] transactions) => Transactions = transactions;

    public TransactionDecodingResult(string error) => Error = error;
}
