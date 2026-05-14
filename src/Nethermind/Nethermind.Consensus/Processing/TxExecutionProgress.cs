// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Shared between the block processor and the prewarmer. The block processor
/// increments the counter after each transaction; the prewarmer skips
/// transactions that have already been executed (like Reth's atomic tx index).
/// </summary>
public sealed class TxExecutionProgress
{
    private int _executedUpTo = -1;

    public void MarkExecuted(int txIndex) => Volatile.Write(ref _executedUpTo, txIndex);

    public bool IsAlreadyExecuted(int txIndex) => Volatile.Read(ref _executedUpTo) >= txIndex;

    public void Reset() => Volatile.Write(ref _executedUpTo, -1);
}
