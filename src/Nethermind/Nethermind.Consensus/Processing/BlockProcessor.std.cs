// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private const int BackgroundReceiptCountThreshold = 16;
    private const int BackgroundLogCountThreshold = 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts) =>
        receipts.Length >= BackgroundReceiptCountThreshold || CountLogs(receipts) >= BackgroundLogCountThreshold;
}
