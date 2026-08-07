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

/// <summary>
/// Simulates the validation prefix of an opaque EIP-8141 frame transaction against the current head,
/// reusing the read-only transaction processing env rather than a pool-specific EVM.
/// </summary>
/// <remarks>
/// Lives here (not in <c>Nethermind.TxPool</c>) because it depends on
/// <see cref="IReadOnlyTxProcessingEnvFactory"/>: TxPool cannot reference Consensus (that would cycle),
/// so the pool depends only on the <see cref="IFrameTxPrefixSimulator"/> abstraction and this
/// composition-root implementation supplies the env. The processing source is created once and reused;
/// simulations are serialized because they share one resettable world state, which also bounds
/// concurrent admission work. Each call runs the prefix under
/// <see cref="ExecutionOptions.FrameValidationPrefixOnly"/> (bounded by <c>MAX_VERIFY_GAS</c>,
/// restoring state) with a <see cref="FrameTxValidationTracer"/> enforcing the trace/opcode rules.
/// EIP8141 follow-ups (design note §4): dependency-set-keyed result caching, head-change re-simulation
/// indexing, a wall-clock cancellation guard, and a multi-simulation admission budget are deferred.
/// https://eips.ethereum.org/EIPS/eip-8141 (ethereum/EIPs#12007)
/// </remarks>
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
                // Cooperative shutdown/scheduler cancel: propagate rather than masking it as a rejection.
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
