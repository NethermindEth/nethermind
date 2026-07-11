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
        if (_inFlight.TryGetValue(block, out Task? recovery) && !recovery.Wait(RecoveryTimeout, token))
        {
            ThrowRecoveryIncomplete(block);
        }
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
                if (!recovery.Wait(RecoveryTimeout)) ThrowRecoveryIncomplete(block);
                break;
            }

            spinner.SpinOnce();
        }
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
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        try
        {
            recoverSignatures.RecoverData(block.Transactions, spec);
            if (_logger.IsDebug)
                _logger.Debug($"newPayload ecrecover blk={block.Number} txs={block.Transactions.Length} " +
                    $"recover={Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms");
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Sender recovery failed for block {block.ToString(Block.Format.FullHashAndNumber)}, retrying once: {e}");
            // This task is the block's only recovery point (the preprocessor skips in-flight
            // blocks); transactions still missing a sender after the retry are rejected by the
            // transaction processor as SenderNotSpecified.
            try
            {
                recoverSignatures.RecoverData(block.Transactions, spec);
            }
            catch (Exception retryException)
            {
                if (_logger.IsError) _logger.Error($"Sender recovery retry failed for block {block.ToString(Block.Format.FullHashAndNumber)}.", retryException);
            }
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRecoveryIncomplete(Block block) =>
        throw new InvalidOperationException($"Streamed sender recovery did not complete for block {block.ToString(Block.Format.FullHashAndNumber)}.");
}
