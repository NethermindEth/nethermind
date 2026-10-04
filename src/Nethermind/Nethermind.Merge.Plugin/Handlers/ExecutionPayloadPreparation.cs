// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Owns decoding work and the worker budget for one payload-processing invocation.
/// </summary>
internal sealed class ExecutionPayloadPreparation(ExecutionPayload payload)
{
    public ParallelUnbalancedWork.WorkerGroup Workers { get; } = new(RuntimeInformation.ProcessorCount);

    /// <param name="totalDifficulty">A total difficulty of the block.</param>
    /// <param name="besideRecovery">
    /// Sender recovery for these transactions is running on <see cref="Workers"/>. A root fanned out on them would wait
    /// behind it, so the root is built on this thread alone instead.
    /// </param>
    public Result<Block> TryGetBlock(UInt256? totalDifficulty = null, bool besideRecovery = false)
    {
        using ParallelUnbalancedWork.WorkerScope workers = Workers.Enter();
        Result<Transaction[]> transactions = payload.TryGetTransactions();
        if (transactions.IsError) return transactions.Error;

        payload.TransactionsRoot ??= TxTrie.CalculateRoot(payload.Transactions, canBeParallel: !besideRecovery);
        return payload.TryGetBlock(totalDifficulty);
    }
}
