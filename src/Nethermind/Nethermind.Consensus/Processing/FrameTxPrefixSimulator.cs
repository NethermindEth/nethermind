// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IFrameTxPrefixSimulator"/>
/// <remarks>Simulations are serialized: they share one resettable world state, which also bounds the
/// concurrent admission work an attacker can trigger.</remarks>
public sealed class FrameTxPrefixSimulator(
    IReadOnlyTxProcessingEnvFactory envFactory,
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    ILogManager logManager) : IFrameTxPrefixSimulator, IDisposable
{
    private static readonly TimeSpan DefaultWallClockBudget = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger = logManager.GetClassLogger<FrameTxPrefixSimulator>();
    private readonly object _lock = new();
    private readonly TimeSpan _wallClockBudget = DefaultWallClockBudget;
    private IReadOnlyTxProcessorSource? _source;
    private bool _disposed;
    private bool _nodeFaultReported;

    internal FrameTxPrefixSimulator(
        IReadOnlyTxProcessingEnvFactory envFactory,
        IBlockFinder blockFinder,
        ISpecProvider specProvider,
        ILogManager logManager,
        TimeSpan wallClockBudget) : this(envFactory, blockFinder, specProvider, logManager) =>
        _wallClockBudget = wallClockBudget;

    public FrameTxSimulationResult Simulate(Transaction tx, bool signaturesPreValidated = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (tx.SenderAddress is null)
        {
            return FrameTxSimulationResult.Reject("sender not recovered");
        }

        BlockHeader? head = blockFinder.Head?.Header;
        if (head is null)
        {
            return FrameTxSimulationResult.Undecided("no chain head to simulate against");
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return FrameTxSimulationResult.Undecided("simulator disposed");
            }

            IReadOnlyTxProcessorSource source = _source ??= envFactory.Create();
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(_wallClockBudget);
            try
            {
                using IReadOnlyTxProcessingScope scope = source.Build(head);
                ITransactionProcessor processor = scope.TransactionProcessor;
                processor.SetBlockExecutionContext(head);

                IReleaseSpec spec = specProvider.GetSpec(head);
                FrameTxValidationTracer tracer = new(tx.SenderAddress, Eip8141Constants.ExpiryVerifierAddress, scope.WorldState, spec, budget.Token);
                ExecutionOptions opts = ExecutionOptions.FrameValidationPrefixOnly;
                if (signaturesPreValidated) opts |= ExecutionOptions.FrameSignaturesPreValidated;
                TransactionResult result = processor.Process(tx, tracer, opts);

                // The EVM ran, so any fault episode has ended and the next one warns again.
                _nodeFaultReported = false;

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
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Frame transaction {tx.Hash} validation-prefix simulation exceeded its {_wallClockBudget.TotalMilliseconds}ms budget; rejecting.");
                return FrameTxSimulationResult.Reject("validation-prefix simulation exceeded its time budget");
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a verdict on the transaction.
                throw;
            }
            catch (Exception e) when (IsNodeFault(e))
            {
                // Blaming the transaction for our own fault feeds the peer flood counter and eventually
                // disconnects honest peers; such faults hit every submission, so warn once per episode.
                if (!_nodeFaultReported)
                {
                    _nodeFaultReported = true;
                    if (_logger.IsWarn) _logger.Warn($"Frame transaction {tx.Hash} validation-prefix simulation hit a node-side fault; leaving it unjudged. Further occurrences log at debug. {e}");
                }
                else if (_logger.IsDebug)
                {
                    _logger.Debug($"Frame transaction {tx.Hash} validation-prefix simulation hit a node-side fault; leaving it unjudged. {e.Message}");
                }

                return FrameTxSimulationResult.Undecided("validation-prefix simulation unavailable");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Attacker-chosen bytecode over the EVM: the throw surface is not enumerable, and one
                // escaping exception would stop admission for every peer.
                if (_logger.IsDebug) _logger.Debug($"Frame transaction {tx.Hash} validation-prefix simulation threw; rejecting. {e}");
                return FrameTxSimulationResult.Reject("validation-prefix simulation error");
            }
        }
    }

    /// <summary>Whether an exception indicts the node rather than the transaction.</summary>
    /// <remarks>The marker covers the <see cref="TrieException"/> family, including nodes pruning can remove.</remarks>
    private static bool IsNodeFault(Exception e) =>
        e is IInternalNethermindException or ObjectDisposedException or IOException;

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
