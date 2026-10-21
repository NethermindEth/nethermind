// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Tracing;

public partial class BlockReceiptsTracer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial Hash256? ReceiptTxHash(Transaction transaction) => transaction.Hash;
}
