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
    private readonly byte[][] _encodedTransactions = payload.Transactions;

    public ParallelUnbalancedWork.WorkerGroup Workers { get; } = new(RuntimeInformation.ProcessorCount);

    public Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        using ParallelUnbalancedWork.WorkerScope workers = Workers.Enter();
        Result<Transaction[]> transactions = payload.TryGetTransactions();
        if (transactions.IsError) return transactions.Error;

        // Built on this thread alone: sender recovery holds the payload's workers by now, and a root fanned out on
        // them would wait for recovery to finish first.
        if (payload.TransactionsRoot is null && ReferenceEquals(_encodedTransactions, payload.Transactions))
            payload.TransactionsRoot = TxTrie.CalculateRoot(_encodedTransactions, canBeParallel: false);

        return payload.TryGetBlock(totalDifficulty);
    }
}
