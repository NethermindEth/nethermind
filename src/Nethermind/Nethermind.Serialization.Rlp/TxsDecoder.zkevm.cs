// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public static partial class TxsDecoder
{
    // Zisk stateless guest builds with --no-pthread; parallelism is unavailable, so always decode sequentially.
    private static TransactionDecodingResult DecodeParallel(byte[][] txData, IRlpDecoder<Transaction> rlpDecoder, bool skipErrors, bool borrowMemory) =>
        DecodeSequential(txData, rlpDecoder, skipErrors, borrowMemory);
}
