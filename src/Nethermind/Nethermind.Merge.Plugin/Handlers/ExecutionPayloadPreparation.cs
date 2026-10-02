// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Owns decoding work and the worker budget for one payload-processing invocation.
/// </summary>
internal sealed class ExecutionPayloadPreparation : IDisposable
{
    private const int MinTxsForBackgroundRoot = 32;

    private readonly ExecutionPayload _payload;
    private readonly byte[][] _encodedTransactions;
    private readonly ParallelUnbalancedWork.BackgroundWork? _txRootWork;
    private Hash256? _txRoot;

    public ParallelUnbalancedWork.WorkerGroup Workers { get; } = new(RuntimeInformation.ProcessorCount);

    public ExecutionPayloadPreparation(ExecutionPayload payload)
    {
        _payload = payload;
        _encodedTransactions = payload.Transactions;
        if (payload.TransactionsRoot is null && _encodedTransactions.Length >= MinTxsForBackgroundRoot && !RuntimeInformation.IsSingleProcessor)
        {
            using ParallelUnbalancedWork.WorkerScope workers = Workers.Enter();
            _txRootWork = ParallelUnbalancedWork.BackgroundFor(0, 1, ParallelUnbalancedWork.DefaultOptions,
                _ => _txRoot = TxTrie.CalculateRoot(_encodedTransactions));
        }
    }

    public Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        using ParallelUnbalancedWork.WorkerScope workers = Workers.Enter();
        Result<Transaction[]> transactions = _payload.TryGetTransactions();
        if (!transactions.IsError && _txRootWork is not null && ReferenceEquals(_encodedTransactions, _payload.Transactions))
        {
            _txRootWork.WaitForCompletion();
            _payload.TransactionsRoot = _txRoot;
        }
        else
        {
            _txRootWork?.Dispose();
        }

        return _payload.TryGetBlock(totalDifficulty);
    }

    public void Dispose() => _txRootWork?.Dispose();
}
