// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;

namespace Nethermind.Serialization.Rlp;

public static partial class TxsDecoder
{
    private static TransactionDecodingResult DecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors, bool borrowMemory)
    {
        Transaction[] decoded = new Transaction[txData.Length];
        bool[] failed = new bool[1];

        ParallelUnbalancedWork.For(
            0,
            txData.Length,
            ParallelUnbalancedWork.DefaultOptions,
            (rlpDecoder, txData, decoded, failed, skipErrors, borrowMemory),
            static (i, state) =>
            {
                try
                {
                    state.decoded[i] = DecodeTransaction(state.rlpDecoder, state.txData[i], state.borrowMemory);
                }
                catch (Exception e) when (state.skipErrors && e is RlpException or ArgumentException)
                {
                }
                catch
                {
                    Volatile.Write(ref state.failed[0], true);
                }

                return state;
            });

        if (Volatile.Read(ref failed[0])) return DecodeSequential(txData, rlpDecoder, skipErrors, borrowMemory);

        return new TransactionDecodingResult(skipErrors ? WithoutUndecoded(decoded) : decoded);
    }

    private static Transaction[] WithoutUndecoded(Transaction[] decoded)
    {
        int added = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            if (decoded[i] is not null) decoded[added++] = decoded[i];
        }

        if (added != decoded.Length) Array.Resize(ref decoded, added);
        return decoded;
    }
}
