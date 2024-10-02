// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Core.Cpu;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Core.Eip2930;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ISpecProvider specProvider, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount * 4);
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount * 4);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public Task PreWarmCaches(Block suggestedBlock, Hash256? parentStateRoot, AccessList? systemTxAccessList, CancellationToken cancellationToken = default)
    {
        if (preBlockCaches is not null)
        {
            if (preBlockCaches.ClearCaches())
            {
                if (_logger.IsWarn) _logger.Warn("Caches are not empty. Clearing them.");
            }

            var physicalCoreCount = RuntimeInformation.PhysicalCoreCount;
            if (!IsGenesisBlock(parentStateRoot) && physicalCoreCount > 2 && !cancellationToken.IsCancellationRequested)
            {
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = physicalCoreCount - 1, CancellationToken = cancellationToken };

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                var addressWarmer = new AddressWarmer(parallelOptions, suggestedBlock, parentStateRoot, systemTxAccessList, this);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass cancellation token to the task, we don't want exceptions to be thrown in main processing thread
                return Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot, parallelOptions, addressWarmer, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    // Parent state root is null for genesis block
    private static bool IsGenesisBlock(Hash256? parentStateRoot) => parentStateRoot is null;

    public bool ClearCaches() => preBlockCaches?.ClearCaches() ?? false;

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);
            WarmupTransactions(parallelOptions, spec, suggestedBlock, parentStateRoot);
            WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parentStateRoot);

            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Warn($"Error pre-warming {suggestedBlock.Number}. {ex}");
        }
        finally
        {
            // Don't compete task until address warmer is also done.
            addressWarmer.Wait();
        }
    }

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                int progress = 0;
                Parallel.For(0, block.Withdrawals.Length, parallelOptions,
                    _ =>
                    {
                        IReadOnlyTxProcessorSource env = _envPool.Get();
                        int i = 0;
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(stateRoot);
                            // Process withdrawals in sequential order, rather than partitioning scheme from Parallel.For
                            // Interlocked.Increment returns the incremented value, so subtract 1 to start at 0
                            i = Interlocked.Increment(ref progress) - 1;
                            scope.WorldState.WarmUp(block.Withdrawals[i].Address);
                        }
                        finally
                        {
                            _envPool.Return(env);
                        }
                    });
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error($"Error pre-warming withdrawal", ex);
        }
    }

    private void WarmupTransactions(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            int progress = 0;
            Parallel.For(0, block.Transactions.Length, parallelOptions, _ =>
            {
                using ThreadExtensions.Disposable handle = Thread.CurrentThread.BoostPriority();
                IReadOnlyTxProcessorSource env = _envPool.Get();
                SystemTransaction systemTransaction = _systemTransactionPool.Get();
                Transaction? tx = null;
                try
                {
                    // Process transactions in sequential order, rather than partitioning scheme from Parallel.For
                    // Interlocked.Increment returns the incremented value, so subtract 1 to start at 0
                    int i = Interlocked.Increment(ref progress) - 1;
                    // If the transaction has already been processed or being processed, exit early
                    if (block.TransactionProcessed > i) return;

                    tx = block.Transactions[i];
                    tx.CopyTo(systemTransaction);
                    using IReadOnlyTxProcessingScope scope = env.Build(stateRoot);
                    if (spec.UseTxAccessLists)
                    {
                        scope.WorldState.WarmUp(tx.AccessList); // eip-2930
                    }
                    TransactionResult result = scope.TransactionProcessor.Trace(systemTransaction, new BlockExecutionContext(block.Header.Clone()), NullTxTracer.Instance);
                    if (_logger.IsTrace) _logger.Trace($"Finished pre-warming cache for tx[{i}] {tx.Hash} with {result}");
                }
                catch (Exception ex) when (ex is EvmException or OverflowException)
                {
                    // Ignore, regular tx processing exceptions
                }
                catch (Exception ex)
                {
                    if (_logger.IsDebug) _logger.Error($"Error pre-warming cache {tx?.Hash}", ex);
                }
                finally
                {
                    _systemTransactionPool.Return(systemTransaction);
                    _envPool.Return(env);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error($"Error pre-warming withdrawal", ex);
        }
    }

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, Hash256 stateRoot, AccessList? systemTxAccessList, BlockCachePreWarmer preWarmer)
        : IThreadPoolWorkItem
    {
        private readonly Block Block = block;
        private readonly Hash256 StateRoot = stateRoot;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly AccessList? SystemTxAccessList = systemTxAccessList;
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

        public void Wait() => _doneEvent.Wait();

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                if (parallelOptions.CancellationToken.IsCancellationRequested) return;
                WarmupAddresses(parallelOptions, Block);
            }
            catch (Exception ex)
            {
                if (PreWarmer._logger.IsDebug) PreWarmer._logger.Error($"Error pre-warming addresses", ex);
            }
            finally
            {
                _doneEvent.Set();
            }
        }

        private void WarmupAddresses(ParallelOptions parallelOptions, Block block)
        {
            if (parallelOptions.CancellationToken.IsCancellationRequested) return;

            try
            {
                if (SystemTxAccessList is not null)
                {
                    var env = PreWarmer._envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.Build(StateRoot);
                        scope.WorldState.WarmUp(SystemTxAccessList);
                    }
                    finally
                    {
                        PreWarmer._envPool.Return(env);
                    }
                }

                int progress = 0;
                Parallel.For(0, block.Transactions.Length, parallelOptions,
                _ =>
                {
                    int i = 0;
                    // Process addresses in sequential order, rather than partitioning scheme from Parallel.For
                    // Interlocked.Increment returns the incremented value, so subtract 1 to start at 0
                    i = Interlocked.Increment(ref progress) - 1;
                    Transaction tx = block.Transactions[i];
                    Address? sender = tx.SenderAddress;

                    var env = PreWarmer._envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.Build(StateRoot);
                        if (sender is not null)
                        {
                            scope.WorldState.WarmUp(sender);
                        }
                        Address to = tx.To;
                        if (to is not null)
                        {
                            scope.WorldState.WarmUp(to);
                        }
                    }
                    finally
                    {
                        PreWarmer._envPool.Return(env);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }
}
