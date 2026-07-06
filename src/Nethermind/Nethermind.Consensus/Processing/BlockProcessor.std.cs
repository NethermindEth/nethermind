// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private const int ReceiptsRootParallelThreshold = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsRootInParallel(int receiptCount) =>
        receiptCount >= ReceiptsRootParallelThreshold;
}
