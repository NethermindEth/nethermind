// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public partial class BlockReceiptsTracer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial Hash256? ReceiptTxHash(Transaction transaction) => transaction.Hash;

    private partial UInt256 ReceiptEffectiveGasPrice(Transaction transaction)
    {
        // Diagnostic-only: effective gas price after EIP-1559 baseFee adjustment.
        // Computed eagerly so the diagnostic dump (which doesn't run through the
        // ReceiptForRpc pipeline) doesn't see effectiveGasPrice as null.
        UInt256 baseFee = Block.Header.BaseFeePerGas;
        return transaction.CalculateEffectiveGasPrice(eip1559Enabled: baseFee > 0, baseFee);
    }
}
