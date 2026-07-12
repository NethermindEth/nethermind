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
/// Owns streamed sender recovery: starts it, lets executors join it, and keeps the pipeline
/// preprocessor from re-recovering blocks already in flight. Tracked per <see cref="BlockBody"/>,
/// not per block: executors receive a header-replaced copy of the suggested block
/// (<c>BlockProcessor.PrepareBlockForProcessing</c>) that shares its body — keying by block
/// instance made executor joins silent no-ops and falsely invalidated a valid block.
/// </summary>
public sealed class StreamedSenderRecovery(
    RecoverSignatures recoverSignatures,
    ISpecProvider specProvider,
    ILogManager logManager) : IStreamedSenderRecovery, IBlockPreprocessorStep
{
    // Defensive only: a stuck recovery must fail the block, never hang the processing pipeline.
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly ConditionalWeakTable<BlockBody, Task> _inFlight = [];
    private readonly ILogger _logger = logManager.GetClassLogger<StreamedSenderRecovery>();

    public void Begin(Block block)
    {
        if (block.Transactions.Length == 0) return;

        _inFlight.AddOrUpdate(block.Body, Task.Run(() => Recover(block)));
    }

    public void EnsureSendersRecovered(Block block, CancellationToken token)
    {
        if (!_inFlight.TryGetValue(block.Body, out Task? recovery)) return;

        AwaitRecovery(block, recovery, token);
        RecoverAnythingMissing(block);
    }

    public void EnsureSenderRecovered(Block block, Transaction transaction)
    {
        if (!_inFlight.TryGetValue(block.Body, out Task? recovery)) return;

        SpinWait spinner = default;
        while (!recovery.IsCompleted)
        {
            if (spinner.NextSpinWillYield)
            {
                AwaitRecovery(block, recovery, CancellationToken.None);
                break;
            }

            spinner.SpinOnce();
        }

        if (!IsFullyRecovered(transaction)) RecoverAnythingMissing(block);
    }

    // The sender alone is not enough: recovery writes it before the EIP-7702 authorities, so a
    // set-code transaction with a visible sender may still be missing authorities — executing it
    // then would silently skip valid tuples and diverge the state root.
    private static bool IsFullyRecovered(Transaction transaction)
    {
        if (transaction.SenderAddress is null) return false;

        if (transaction.HasAuthorizationList)
        {
            foreach (AuthorizationTuple tuple in transaction.AuthorizationList.AsSpan())
            {
                if (tuple.Authority is null) return false;
            }
        }

        return true;
    }

    public void RecoverData(Block block)
    {
        // Recovering an in-flight block here would re-create the barrier the streaming removes.
        if (_inFlight.TryGetValue(block.Body, out _)) return;

        recoverSignatures.RecoverData(block);
    }

    private void Recover(Block block)
    {
        try
        {
            recoverSignatures.RecoverData(block.Transactions, specProvider.GetSpec(block.Header));
        }
        catch (Exception e)
        {
            // The joins recover anything left behind, degrading to synchronous recovery.
            if (_logger.IsWarn) _logger.Warn($"Streamed sender recovery failed for block {block.ToString(Block.Format.FullHashAndNumber)}: {e}");
        }
    }

    // A cancelled token throws OperationCanceledException past the fallback: processing is
    // being aborted and no verdict is produced, which is the correct fail mode.
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

    // Fail-closed backstop: whatever happened to the streamed task, execution must see exactly
    // what a non-streamed block would. The warn means a streaming invariant was broken.
    private void RecoverAnythingMissing(Block block)
    {
        int missing = 0;
        foreach (Transaction tx in block.Transactions)
        {
            if (!IsFullyRecovered(tx)) missing++;
        }

        if (missing == 0) return;

        if (_logger.IsWarn)
        {
            string taskState = _inFlight.TryGetValue(block.Body, out Task? recovery)
                ? $"tracked, status={recovery.Status}, exception={recovery.Exception?.GetBaseException().Message ?? "none"}"
                : "not tracked";
            _logger.Warn($"Streamed recovery left {missing}/{block.Transactions.Length} transactions not fully recovered for block " +
                $"{block.ToString(Block.Format.FullHashAndNumber)} (recovery task: {taskState}); recovering synchronously.");
        }

        recoverSignatures.RecoverData(block);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRecoveryIncomplete(Block block) =>
        throw new InvalidOperationException($"Streamed sender recovery did not complete for block {block.ToString(Block.Format.FullHashAndNumber)}.");
}
