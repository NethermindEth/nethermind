// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.TxPool;
using Metrics = Nethermind.TxPool.Metrics;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IFrameTxPrefixSimulator"/>
/// <remarks>Admission work is bounded three ways: <c>MAX_VERIFY_GAS</c> per prefix, a wall-clock timeout
/// per simulation (which also caps the wait for the serialized env), and a cumulative per-head budget.</remarks>
public sealed class FrameTxPrefixSimulator(
    IReadOnlyTxProcessingEnvFactory envFactory,
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    ITxPoolConfig txPoolConfig,
    ILogManager logManager) : IFrameTxPrefixSimulator, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<FrameTxPrefixSimulator>();
    private readonly object _lock = new();
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(txPoolConfig.FrameTxSimulationTimeoutMs);
    private readonly long _headBudgetTicks = (long)(txPoolConfig.FrameTxSimulationBudgetPerHeadMs / 1000d * Stopwatch.Frequency);
    private IReadOnlyTxProcessorSource? _source;
    private Hash256? _budgetHead;
    private long _budgetSpentTicks;
    private bool _disposed;

    public FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default, bool local = false)
    {
        token.ThrowIfCancellationRequested();

        // Public API, so the precondition cannot rely on the pool filter: any other type would run as an
        // ordinary transaction whose mutations this read-only env would not restore.
        if (!tx.SupportsFrames)
        {
            return FrameTxSimulationResult.Reject("not a frame transaction");
        }

        if (tx.SenderAddress is null)
        {
            return FrameTxSimulationResult.Reject("sender not recovered");
        }

        BlockHeader? head = blockFinder.Head?.Header;
        if (head is null)
        {
            return FrameTxSimulationResult.Undecided("no chain head to simulate against");
        }

        // Bounded wait: an admission thread must not queue indefinitely behind other peers' simulations.
        if (!Monitor.TryEnter(_lock, _timeout > TimeSpan.Zero ? _timeout : Timeout.InfiniteTimeSpan))
        {
            Metrics.FrameTxSimulationsBusy++;
            return FrameTxSimulationResult.RejectIndeterminate("validation-prefix simulator busy");
        }

        try
        {
            if (_disposed)
            {
                return FrameTxSimulationResult.Undecided("simulator disposed");
            }

            // The budget rations simulation between gossiping peers; a local submission is not competing
            // for that share, so it is only bounded by the timeout and MAX_VERIFY_GAS.
            if (!local && !HasHeadBudget(head))
            {
                Metrics.FrameTxSimulationsBudgetExhausted++;
                return FrameTxSimulationResult.RejectIndeterminate("validation-prefix simulation budget exhausted for this head");
            }

            long startedAt = Stopwatch.GetTimestamp();
            try
            {
                return SimulateLocked(tx, head, token);
            }
            finally
            {
                _budgetSpentTicks += Stopwatch.GetTimestamp() - startedAt;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private FrameTxSimulationResult SimulateLocked(Transaction tx, BlockHeader head, CancellationToken token)
    {
        FrameTxValidationTracer? tracer = null;
        try
        {
            // Inside the try: a processing env this node cannot build must reject rather than escape into
            // the admission path.
            IReadOnlyTxProcessorSource source = _source ??= envFactory.Create();
            using IReadOnlyTxProcessingScope scope = source.Build(head);
            ITransactionProcessor processor = scope.TransactionProcessor;
            processor.SetBlockExecutionContext(head);

            IReleaseSpec spec = specProvider.GetSpec(head);
            tracer = new FrameTxValidationTracer(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, scope.WorldState, spec, token, _timeout);
            TransactionResult result = processor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly);
            Metrics.FrameTxSimulations++;

            if (tracer.Violated)
            {
                return FrameTxSimulationResult.Reject(tracer.ViolationReason ?? "validation trace rule violated");
            }

            if (!result || tracer.Payer is null)
            {
                return FrameTxSimulationResult.Reject(result.TransactionExecuted ? "validation prefix set no payer" : result.ErrorDescription);
            }

            return FrameTxSimulationResult.Accept(tracer.Payer);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && tracer is { Violated: true })
        {
            // The tracer aborted the interpreter on a rule violation.
            Metrics.FrameTxSimulations++;
            return FrameTxSimulationResult.Reject(tracer.ViolationReason!);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && tracer is { TimedOut: true })
        {
            Metrics.FrameTxSimulations++;
            Metrics.FrameTxSimulationsTimedOut++;
            return FrameTxSimulationResult.RejectTimedOut("validation-prefix simulation timed out");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Neither the caller's cancellation nor one the tracer raised, so it came from this node's
            // env (a cancellable state read during shutdown) and decides nothing about the prefix.
            return FrameTxSimulationResult.RejectIndeterminate("validation-prefix simulation cancelled");
        }
        catch (Exception e) when (IsNodeFault(e))
        {
            // A fault this node is answerable for, wherever it surfaced: a trie read can fail mid-execution,
            // long after the tracer exists, and blaming the transaction for it throttles honest peers.
            if (_logger.IsWarn) _logger.Warn($"Frame transaction {tx.Hash} validation-prefix simulation hit a node-side fault; leaving it unjudged. {e}");
            return FrameTxSimulationResult.Undecided("validation-prefix simulation unavailable");
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // Attacker-chosen bytecode over env build, trie reads and the EVM: the throw surface is not
            // enumerable. Once the tracer exists the prefix is the expected source, so the rejection is
            // definite: one that throws every head cannot pin a slot.
            if (_logger.IsDebug) _logger.Debug($"Frame transaction {tx.Hash} validation-prefix simulation threw; rejecting. {e}");
            return tracer is null
                ? FrameTxSimulationResult.RejectIndeterminate("validation-prefix processing env unavailable")
                : FrameTxSimulationResult.Reject("validation-prefix simulation error");
        }
    }

    /// <summary>Whether an exception indicts the node rather than the transaction.</summary>
    /// <remarks>The marker covers the <see cref="TrieException"/> family, including nodes pruning can remove.</remarks>
    private static bool IsNodeFault(Exception e) =>
        e is IInternalNethermindException or ObjectDisposedException or IOException;

    /// <summary>Whether the per-head simulation time budget still has room, resetting it on a new head.</summary>
    /// <remarks>Checked before the simulation and charged after it, so it can overshoot by one simulation;
    /// a stalled head keeps its exhausted budget. A reorg re-admits its transactions as gossip, so they are
    /// the first claim on the new head's budget, spent on the head-change thread.</remarks>
    private bool HasHeadBudget(BlockHeader head)
    {
        if (_headBudgetTicks <= 0) return true;

        if (_budgetHead != head.Hash)
        {
            _budgetHead = head.Hash;
            _budgetSpentTicks = 0;
        }

        return _budgetSpentTicks < _headBudgetTicks;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _source?.Dispose();
            _source = null;
        }
    }
}
