// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Warms the database read caches during the idle gap between blocks by speculatively executing the
/// current transaction-pool transactions against the chain head. The execution result is discarded —
/// the only effect sought is the side effect of loading the touched state from the database, which
/// populates the process-wide RocksDB block cache and the OS page cache. When the next block arrives,
/// the transactions it shares with the public mempool (typically the majority) hit warm caches instead
/// of paying cold random-read latency on rarely-touched contracts.
///
/// Correctness: the warming is value-safe regardless of reorg. It runs in fresh read-only world-state
/// scopes at the head's state root and never touches the shared block-processing caches
/// (<c>PreBlockCaches</c>); the database caches it warms hold immutable on-disk blocks, not
/// version-specific values. A reorg merely means some warmed blocks go unused.
/// </summary>
public sealed class IdleTxPoolPreWarmer : IDisposable
{
    private const int MaxWarmedTransactions = 4096;

    private readonly Lazy<IReadOnlyTxProcessingEnvFactory> _envFactory;
    private readonly ITxPool _txPool;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly int _concurrency;
    private readonly ILogger _logger;

    // Cancellation source of the in-flight warming pass. Swapped atomically on each head/suggest event.
    // It is intentionally never disposed: no WaitHandle is ever allocated so nothing leaks, and skipping
    // disposal avoids a cancel-after-dispose race with the background pass that observes the token.
    private CancellationTokenSource? _currentPass;

    public IdleTxPoolPreWarmer(
        ILifetimeScope context,
        ITxPool txPool,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
    {
        _envFactory = new Lazy<IReadOnlyTxProcessingEnvFactory>(context.Resolve<IReadOnlyTxProcessingEnvFactory>);
        _txPool = txPool;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _concurrency = blocksConfig.PreWarmStateConcurrency == 0
            ? Math.Min(Environment.ProcessorCount - 1, 16)
            : blocksConfig.PreWarmStateConcurrency;
        _logger = logManager.GetClassLogger<IdleTxPoolPreWarmer>();

        _blockTree.NewHeadBlock += OnNewHeadBlock;
        _blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
    }

    // A new block is about to be processed: stop idle warming so it does not contend with block
    // processing for cores and IO bandwidth.
    private void OnNewSuggestedBlock(object? sender, BlockEventArgs e) => CancelCurrentPass();

    // The head has settled: warm the pool against it during the idle gap until the next block arrives.
    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        BlockHeader head = e.Block.Header;
        CancellationTokenSource pass = new();
        Interlocked.Exchange(ref _currentPass, pass)?.Cancel();
        Task.Run(() => WarmPool(head, pass.Token), pass.Token);
    }

    private void CancelCurrentPass() => Interlocked.Exchange(ref _currentPass, null)?.Cancel();

    private void WarmPool(BlockHeader head, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) return;

            IReleaseSpec spec = _specProvider.GetSpec(head);
            BlockExecutionContext blockContext = new(head, spec);
            IDictionary<AddressAsKey, Transaction[]> bySender =
                _txPool.GetPendingTransactionsBySender(filterToReadyTx: true, head.BaseFeePerGas);
            if (bySender.Count == 0) return;

            WarmSenderGroups(head, bySender.Values, blockContext, token);
        }
        catch (OperationCanceledException)
        {
            // The next block arrived; the partially-completed warming is exactly what we wanted.
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Idle tx-pool prewarm failed for head {head.Number}: {ex}");
        }
    }

    private void WarmSenderGroups(
        BlockHeader head,
        ICollection<Transaction[]> senderGroups,
        BlockExecutionContext blockContext,
        CancellationToken token)
    {
        int warmedCount = 0;
        ParallelOptions options = new() { MaxDegreeOfParallelism = _concurrency };

        Parallel.ForEach(
            senderGroups,
            options,
            () => CreateScope(head, blockContext),
            (senderTransactions, loopState, scope) =>
            {
                foreach (Transaction transaction in senderTransactions)
                {
                    if (token.IsCancellationRequested || Interlocked.Increment(ref warmedCount) > MaxWarmedTransactions)
                    {
                        loopState.Stop();
                        break;
                    }

                    WarmTransaction(scope.Scope, transaction);
                }

                return scope;
            },
            ReturnScope);
    }

    private TxScope CreateScope(BlockHeader head, in BlockExecutionContext blockContext)
    {
        IReadOnlyTxProcessorSource source = _envFactory.Value.Create();
        IReadOnlyTxProcessingScope scope = source.Build(head);
        scope.TransactionProcessor.SetBlockExecutionContext(in blockContext);
        return new TxScope(source, scope);
    }

    private static void WarmTransaction(IReadOnlyTxProcessingScope scope, Transaction transaction)
    {
        try
        {
            scope.TransactionProcessor.Warmup(transaction, NullTxTracer.Instance);
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            // Expected for transactions that revert or overflow during speculative execution.
        }
        catch (Exception)
        {
            // Warming is best-effort; a single failing transaction must never abort the pass.
        }
    }

    private static void ReturnScope(TxScope scope) => scope.Dispose();

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _blockTree.NewSuggestedBlock -= OnNewSuggestedBlock;
        CancelCurrentPass();
    }

    private readonly struct TxScope(IReadOnlyTxProcessorSource source, IReadOnlyTxProcessingScope scope) : IDisposable
    {
        public IReadOnlyTxProcessingScope Scope { get; } = scope;

        public void Dispose()
        {
            Scope.Dispose();
            source.Dispose();
        }
    }
}
