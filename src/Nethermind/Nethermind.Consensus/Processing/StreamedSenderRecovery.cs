// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The single owner of streamed sender recovery: starts it, lets executors join it, and keeps
/// the pipeline preprocessor from re-recovering blocks whose recovery is already in flight.
/// In-flight recoveries are tracked per block instance, so duplicate payloads (distinct
/// <see cref="Block"/> instances for the same hash) each recover their own transactions, and
/// entries vanish with their blocks.
/// </summary>
public sealed class StreamedSenderRecovery(
    RecoverSignatures recoverSignatures,
    ISpecProvider specProvider,
    ILogManager logManager) : IStreamedSenderRecovery, IBlockPreprocessorStep
{
    /// <summary>
    /// Defensive only: the recovery task is pure computation and always completes. A stuck task
    /// must fail the block (the consensus client retries it), never hang the processing pipeline.
    /// </summary>
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly ConditionalWeakTable<Block, Task> _inFlight = [];
    private readonly ILogger _logger = logManager.GetClassLogger<StreamedSenderRecovery>();

    public void Begin(Block block)
    {
        if (block.Transactions.Length == 0) return;

        _inFlight.Add(block, Task.Run(() => Recover(block)));
    }

    public void EnsureSendersRecovered(Block block, CancellationToken token)
    {
        if (!_inFlight.TryGetValue(block, out Task? recovery)) return;

        AwaitRecovery(block, recovery, token);
        RecoverAnythingMissing(block);
    }

    public void EnsureSenderRecovered(Block block, Transaction transaction)
    {
        if (transaction.SenderAddress is not null) return;
        if (!_inFlight.TryGetValue(block, out Task? recovery)) return;

        // Execution normally runs well behind recovery, so the spin is rare and short; past it,
        // the sender is genuinely behind and blocking on the whole task is cheaper than yielding.
        SpinWait spinner = default;
        while (transaction.SenderAddress is null && !recovery.IsCompleted)
        {
            if (spinner.NextSpinWillYield)
            {
                AwaitRecovery(block, recovery, CancellationToken.None);
                break;
            }

            spinner.SpinOnce();
        }

        if (transaction.SenderAddress is null) RecoverAnythingMissing(block);
    }

    public void RecoverData(Block block)
    {
        // A block with recovery in flight is completed by the executors' Ensure* calls;
        // recovering here would re-create the pipeline barrier the streaming removes.
        if (_inFlight.TryGetValue(block, out _)) return;

        recoverSignatures.RecoverData(block);
    }

    private void Recover(Block block)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            recoverSignatures.RecoverData(block.Transactions, specProvider.GetSpec(block.Header));
            if (_logger.IsDebug)
                _logger.Debug($"newPayload ecrecover blk={block.Number} txs={block.Transactions.Length} " +
                    $"recover={Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms");
        }
        catch (Exception e)
        {
            // The executors' Ensure* joins recover anything this task left behind, so a failure
            // here degrades to synchronous recovery at execution time, never to a wrong verdict.
            if (_logger.IsWarn) _logger.Warn($"Streamed sender recovery failed for block {block.ToString(Block.Format.FullHashAndNumber)}: {e}");
        }
    }

    private void AwaitRecovery(Block block, Task recovery, CancellationToken token)
    {
        try
        {
            if (!recovery.Wait(RecoveryTimeout, token)) ThrowRecoveryIncomplete(block);
        }
        catch (AggregateException)
        {
            // Already logged by Recover; the caller falls back to synchronous recovery.
        }
    }

    /// <summary>
    /// The fail-closed backstop: whatever happened to the streamed task — faulted, timed out,
    /// wiring gaps, anything unforeseen — execution must see the exact senders a non-streamed
    /// block would, so any transaction still missing one is recovered synchronously here.
    /// Truly invalid signatures still end up with a null sender and are rejected as before.
    /// A warn with the full context makes any such occurrence diagnosable after the fact:
    /// the backstop engaging at all means an invariant of the streaming design was broken.
    /// </summary>
    private void RecoverAnythingMissing(Block block)
    {
        int missing = 0;
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.SenderAddress is null) missing++;
        }

        if (missing == 0) return;

        if (_logger.IsWarn)
        {
            string taskState = _inFlight.TryGetValue(block, out Task? recovery)
                ? $"tracked, status={recovery.Status}, exception={recovery.Exception?.GetBaseException().Message ?? "none"}"
                : "not tracked";
            _logger.Warn($"Streamed recovery left {missing}/{block.Transactions.Length} senders missing for block " +
                $"{block.ToString(Block.Format.FullHashAndNumber)} (recovery task: {taskState}); recovering synchronously.");
        }

        recoverSignatures.RecoverData(block);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRecoveryIncomplete(Block block) =>
        throw new InvalidOperationException($"Streamed sender recovery did not complete for block {block.ToString(Block.Format.FullHashAndNumber)}.");
}
