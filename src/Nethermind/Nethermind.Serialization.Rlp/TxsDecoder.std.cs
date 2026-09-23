// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            (rlpDecoder, txData, decoded, failed, borrowMemory),
            static (i, state) =>
            {
                try
                {
                    state.decoded[i] = DecodeTransaction(state.rlpDecoder, state.txData[i], state.borrowMemory);
                }
                catch
                {
                    // Defer to the serial fallback, which reproduces the exact single-threaded error
                    // behavior (first invalid index, exception surface) and applies skipErrors.
                    Volatile.Write(ref state.failed[0], true);
                }

                return state;
            });

        return Volatile.Read(ref failed[0])
            ? DecodeSequential(txData, rlpDecoder, skipErrors, borrowMemory)
            : new TransactionDecodingResult(decoded);
    }
}
