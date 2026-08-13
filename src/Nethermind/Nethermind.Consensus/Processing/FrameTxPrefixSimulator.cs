// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
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
    private readonly ILogger _logger = logManager.GetClassLogger<FrameTxPrefixSimulator>();
    private readonly object _lock = new();
    private IReadOnlyTxProcessorSource? _source;
    private bool _disposed;

    public FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (tx.SenderAddress is null)
        {
            return FrameTxSimulationResult.Reject("sender not recovered");
        }

        BlockHeader? head = blockFinder.Head?.Header;
        if (head is null)
        {
            return FrameTxSimulationResult.Reject("no chain head to simulate against");
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return FrameTxSimulationResult.Reject("simulator disposed");
            }

            IReadOnlyTxProcessorSource source = _source ??= envFactory.Create();
            try
            {
                using IReadOnlyTxProcessingScope scope = source.Build(head);
                ITransactionProcessor processor = scope.TransactionProcessor;
                processor.SetBlockExecutionContext(head);

                IReleaseSpec spec = specProvider.GetSpec(head);
                FrameTxValidationTracer tracer = new(tx.SenderAddress, Eip8141Constants.ExpiryVerifierAddress, scope.WorldState, spec);
                TransactionResult result = processor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly);

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
            catch (OperationCanceledException)
            {
                // Shutdown, not a verdict on the transaction.
                throw;
            }
            catch (Exception e)
            {
                // A malformed opaque prefix must never crash admission: reject and keep the pool up.
                if (_logger.IsDebug) _logger.Debug($"Frame transaction {tx.Hash} validation-prefix simulation threw; rejecting. {e}");
                return FrameTxSimulationResult.Reject("validation-prefix simulation error");
            }
        }
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
