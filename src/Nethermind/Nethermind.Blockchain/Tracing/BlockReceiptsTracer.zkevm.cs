// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public partial class BlockReceiptsTracer
{
    /// <inheritdoc cref="ReceiptTxHash"/>
    /// <remarks>
    /// The guest reads only consensus fields of its receipts (receipts root, bloom, block validation and
    /// execution requests), none of which covers the transaction hash, and it stores no receipts: leaving
    /// the hash unset saves a keccak of every encoded transaction.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial Hash256? ReceiptTxHash(Transaction transaction) => null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial UInt256 ReceiptEffectiveGasPrice(Transaction transaction) => default;
}
