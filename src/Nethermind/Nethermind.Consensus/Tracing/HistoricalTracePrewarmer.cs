// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Tracing;

public sealed record HistoricalTracePrewarmSettings(int Concurrency);

public sealed class HistoricalTracePrewarmer(
    IReadOnlyTxProcessingEnvFactory environmentFactory,
    IBlockTree blockTree,
    IEthereumEcdsa ecdsa,
    HistoricalTracePrewarmSettings settings,
    ILogManager logManager)
{
    private const int MinimumBlockDistance = 256;
    private const int TimeoutMilliseconds = 5_000;
    private const long MaximumTransactionGas = 8_000_000;
    private readonly int _concurrency = Math.Clamp(settings.Concurrency, 0, Environment.ProcessorCount);
    private readonly ILogger _logger = logManager.GetClassLogger<HistoricalTracePrewarmer>();
    private int _active;

    public void Prewarm(Block block, BlockHeader? parent, CancellationToken cancellationToken)
    {
        if (_concurrency == 0 || parent is null || block.Transactions.Length == 0
            || blockTree.Head is not { } head || block.Number > head.Number
            || head.Number - block.Number < MinimumBlockDistance
            || block.Hash is null || !blockTree.IsMainChain(block.Hash, throwOnMissingHash: false))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return;

        try
        {
            PrewarmCore(block, parent, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Historical trace prewarming budget expired for block {block.Number}.");
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_logger.IsDebug) _logger.Debug($"Historical trace prewarming skipped for block {block.Number}: {exception}");
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PrewarmCore(Block block, BlockHeader parent, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeoutMilliseconds);
        ParallelOptions options = new() { MaxDegreeOfParallelism = _concurrency, CancellationToken = budget.Token };
        CancellationTxTracer tracer = new(NullTxTracer.Instance, budget.Token);

        ParallelUnbalancedWork.For(0, block.Transactions.Length, options,
            environmentFactory.Create,
            (index, environment) =>
            {
                WarmTransaction(block.Transactions[index], block.Header, parent, environment, tracer);
                return environment;
            },
            static environment => environment.Dispose());
    }

    private void WarmTransaction(Transaction source, BlockHeader header, BlockHeader parent,
        IReadOnlyTxProcessorSource environment, ITxTracer tracer)
    {
        if (source.GetType() != typeof(Transaction)
            || source.Type is not (TxType.Legacy or TxType.AccessList or TxType.EIP1559)
            || source.AuthorizationList is not null || source.IsOPSystemTransaction || source.IsServiceTransaction)
        {
            return;
        }

        Transaction transaction = new();
        source.CopyTo(transaction, copyHash: false);
        transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
        if (transaction.SenderAddress is null) return;
        transaction.GasLimit = Math.Min(transaction.GasLimit, MaximumTransactionGas);

        using IReadOnlyTxProcessingScope scope = environment.Build(parent);
        scope.TransactionProcessor.SetBlockExecutionContext(header.Clone());
        scope.TransactionProcessor.Warmup(transaction, tracer);
    }
}
