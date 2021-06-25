//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamBlockchainProcessor : IDisposable
    {
        private readonly IReadOnlyDbProvider _readOnlyDbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly ILogger _logger;

        private readonly IBlockProcessingQueue _standardProcessorQueue;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly IReadOnlyBlockTree _readOnlyBlockTree;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;

        public BeamBlockchainProcessor(
            IReadOnlyDbProvider readOnlyDbProvider,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IBlockProcessingQueue processingQueue,
            ISyncModeSelector syncModeSelector)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _standardProcessorQueue = processingQueue ?? throw new ArgumentNullException(nameof(processingQueue));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _readOnlyBlockTree = new ReadOnlyBlockTree(blockTree);
            blockTree.NewBestSuggestedBlock += OnNewBlock;
            _logger = logManager.GetClassLogger();
            _blockAction = BeamProcess;

            _syncModeSelector.Preparing += SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing += SyncModeSelectorOnChanging;
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private Action<Block> _blockAction;

        private Queue<Block> _shelvedBlocks = new();

        private void EnqueueForStandardProcessing(Block block)
        {
            while (_shelvedBlocks.TryDequeue(out Block? shelvedBlock))
            {
                if(_logger.IsInfo) _logger.Info($"Enqueuing previously shelved block {shelvedBlock.ToString(Block.Format.Short)}");
                _standardProcessorQueue.Enqueue(shelvedBlock, ProcessingOptions.StoreReceipts);
            }

            if(_logger.IsDebug) _logger.Debug("Enqueuing block for standard processing (skipping beam in full sync)");
            _standardProcessorQueue.Enqueue(block, ProcessingOptions.StoreReceipts);
        }

        private void Shelve(Block block)
        {
            if(_logger.IsInfo) _logger.Info($"Shelving block {block.ToString(Block.Format.Short)} while beam processor transitions to full processor.");
            _shelvedBlocks.Enqueue(block);
        }

        private object _transitionLock = new();

        private bool _isAfterBeam;

        private void SyncModeSelectorOnPreparing(object? sender, SyncModeChangedEventArgs e)
        {
            if (e.IsBeamSyncFinished())
            {
                lock (_transitionLock)
                {
                    if (_isAfterBeam)
                    {
                        // we do it only once - later we stay forever in full sync mode
                        return;
                    }

                    _isAfterBeam = true;
                    if(_logger.IsInfo) _logger.Info("Setting block action to shelving.");
                    _blockAction = Shelve;
                }

                CancelAllBeamSyncTasks();
                Task.WhenAll(_beamProcessTasks).Wait(); // sync mode selector is waiting for beam syncing blocks to stop
            }
        }

        private void SyncModeSelectorOnChanging(object? sender, SyncModeChangedEventArgs e)
        {
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (e.IsBeamSyncFinished())
            {
                if(_logger.IsInfo) _logger.Info("Setting block action to standard processing.");
                _blockAction = EnqueueForStandardProcessing;
                UnregisterListeners();
            }
        }

        /// <summary>
        /// Whenever we finish beam syncing one of the blocks we cancel all previous ones
        /// and move our processing power to the future
        /// </summary>
        /// <param name="number">Number of the block that we have just processed</param>
        private void CancelPreviousBeamSyncingBlocks(long number)
        {
            lock (_tokens)
            {
                for (int i = 64; i > 0; i--)
                {
                    if (_tokens.TryGetValue(number - i, out CancellationTokenSource? token))
                    {
                        token.Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// Whenever we start beam syncing we want to ensure that we stop all the blocks
        /// that did not process for long time (any blocks older than 6)
        /// </summary>
        /// <param name="number"></param>
        private void CancelOldBeamTasks(long number)
        {
            lock (_tokens)
            {
                for (int i = 64; i > 6; i--)
                {
                    if (_tokens.TryGetValue(number - i, out CancellationTokenSource? token))
                    {
                        token.Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// When we transition to full sync we want to cancel all the beam sync tasks
        /// </summary>
        private void CancelAllBeamSyncTasks()
        {
            lock (_tokens)
            {
                foreach (KeyValuePair<long, CancellationTokenSource> cancellationTokenSource in _tokens)
                {
                    cancellationTokenSource.Value.Cancel();
                }
            }
        }

        private (IBlockchainProcessor, IStateReader) CreateProcessor(Block block, IReadOnlyDbProvider readOnlyDbProvider, ISpecProvider specProvider, ILogManager logManager)
        {
            // TODO: need to pass the state with cache
            ReadOnlyTxProcessingEnv txEnv = new(readOnlyDbProvider, new TrieStore(readOnlyDbProvider.StateDb, logManager).AsReadOnly(readOnlyDbProvider.StateDb), _readOnlyBlockTree, specProvider, logManager);
            ReadOnlyChainProcessingEnv env = new(txEnv, _blockValidator, _recoveryStep, _rewardCalculatorSource.Get(txEnv.TransactionProcessor), NullReceiptStorage.Instance, _readOnlyDbProvider, specProvider, logManager);
            env.BlockProcessor.TransactionProcessed += (_, args) =>
            {
                Interlocked.Increment(ref Metrics.BeamedTransactions);
                if (_logger.IsInfo) _logger.Info($"Processed tx {args.Index + 1}/{block.Transactions.Length} of {block.Number}");
            };

            return (env.ChainProcessor, txEnv.StateReader);
        }

        private ConcurrentBag<Task> _beamProcessTasks = new();

        private void OnNewBlock(object? sender, BlockEventArgs e)
        {
            Block block = e.Block;
            if (block.IsGenesis)
            {
                EnqueueForStandardProcessing(block);
                return;
            }

            lock (_transitionLock)
            {
                _blockAction(block);
            }
        }

        /// <summary>
        /// Tokens really should be by hash or hash sets by number
        /// </summary>
        private ConcurrentDictionary<long, CancellationTokenSource> _tokens = new();

        private void BeamProcess(Block block)
        {
            if (block.TotalDifficulty == null)
            {
                throw new InvalidDataException(
                    $"Received a block with null {nameof(block.TotalDifficulty)} for beam processing");
            }
            
            CancellationTokenSource cancellationToken;
            lock (_tokens)
            {
                cancellationToken = _tokens.GetOrAdd(block.Number, t => new CancellationTokenSource());
                if (_isDisposed)
                {
                    return;
                }
            }

            Task beamProcessingTask = Task.CompletedTask;
            Task prefetchTasks = Task.CompletedTask;

            try
            {
                if (_logger.IsInfo) _logger.Info($"Beam processing block {block}");
                _recoveryStep.RecoverData(block);
                (IBlockchainProcessor beamProcessor, IStateReader stateReader) = CreateProcessor(block, new ReadOnlyDbProvider(_readOnlyDbProvider, true), _specProvider, _logManager);

                BlockHeader parentHeader = _readOnlyBlockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (parentHeader != null)
                {
                    // TODO: is author / beneficiary always resolved here
                    prefetchTasks = PrefetchNew(stateReader, block, parentHeader.StateRoot!, parentHeader.Author ?? parentHeader.Beneficiary!);
                }
                
                Stopwatch stopwatch = Stopwatch.StartNew();
                Block? processedBlock = null;
                beamProcessingTask = Task.Run(() =>
                {
                    BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                    BeamSyncContext.Description.Value = $"[preProcess of {block.Hash!.ToShortString()}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                    processedBlock = beamProcessor.Process(block, ProcessingOptions.Beam, NullBlockTracer.Instance);
                    stopwatch.Stop();
                    if (processedBlock == null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block {block.ToString(Block.Format.Short)} skipped in beam sync");
                    }
                    else
                    {
                        Interlocked.Increment(ref Metrics.BeamedBlocks);
                        if(_logger.IsInfo) _logger.Info($"Successfully beam processed block {processedBlock.ToString(Block.Format.Short)} in {stopwatch.ElapsedMilliseconds}ms");
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsInfo) _logger.Info($"Stopped processing block {block} | {t.Exception?.Flatten().InnerException?.Message}");
                        if (_logger.IsTrace) _logger.Trace($"Details of beam sync failure {block} | {t.Exception}");
                        return;
                    }

                    if (processedBlock != null)
                    {
                        // if (_logger.IsDebug) _logger.Debug($"Running standard processor after beam sync for {block}");
                        // at this stage we are sure to have all the state available
                        CancelPreviousBeamSyncingBlocks(processedBlock.Number);
                        
                        // do I even need this?
                        // do I even need to process any of these blocks or just leave the RPC available
                        // (based on user expectations they may need to trace or just query balance)
                        
                        // soo - there should be a separate beam queue that we can wait for to finish?
                        // then we can ensure that it finishes before the normal queue fires
                        // and so they never hit the wrong databases?
                        // but, yeah, we do not even need to process it twice
                        // we can just announce that we have finished beam processing here...
                        // _standardProcessorQueue.Enqueue(block, ProcessingOptions.Beam);
                        // I only needed it in the past when I wanted to actually store the beam data
                        // now I can generate the witness on the fly and transfer the witness to the right place...
                        // OK, seems fine
                    }

                    beamProcessor.Dispose();
                });
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Block {block.ToString(Block.Format.Short)} failed processing and it will be skipped from beam sync", e);
            }

            _beamProcessTasks.Add(Task.WhenAll(beamProcessingTask, prefetchTasks));

            long number = block.Number;
            CancelOldBeamTasks(number);
        }

        private Task PrefetchNew(IStateReader stateReader, Block block, Keccak stateRoot, Address miner)
        {
            if (block.TotalDifficulty == null)
            {
                throw new InvalidDataException(
                    $"Received a block with null {nameof(block.TotalDifficulty)} for beam processing");
            }
            
            CancellationTokenSource cancellationToken;
            lock (_tokens)
            {
                cancellationToken = _tokens.GetOrAdd(block.Number, t => new CancellationTokenSource());
                if (_isDisposed)
                {
                    return Task.CompletedTask;
                }
            }

            string description = $"[miner {miner}]";
            Task minerTask = Task.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                BeamSyncContext.Description.Value = description;
                BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                stateReader.GetAccount(stateRoot, miner);
                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(
                    t.IsFaulted ? $"{description} prefetch failed {t.Exception?.Message}" : $"{description} prefetch complete - resolved {t.Result}");
            });

            Task senderTask = Task.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                for (int i = 0; i < block.Transactions!.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    BeamSyncContext.Description.Value = $"[tx prefetch {i}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    
                    // TODO: is SenderAddress for sure resolved here?
                    stateReader.GetAccount(stateRoot, tx.SenderAddress!);
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(
                    t.IsFaulted ? $"tx prefetch failed {t.Exception?.Message}" : $"tx prefetch complete - resolved {t.Result}");
            });

            Task storageTask = Task.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                for (int i = 0; i < block.Transactions!.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    if (tx.To != null)
                    {
                        BeamSyncContext.Description.Value = $"[storage prefetch {i}]";
                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        
                        // TODO: not that this does not retrieve storage
                        // we should call GetStorage afterwards
                        _ = stateReader.GetAccount(stateRoot, tx.To)?.StorageRoot;
                    }
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"storage prefetch failed {t.Exception?.Message}" : $"storage prefetch complete - resolved {t.Result}");
            });


            Task codeTask = Task.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                for (int i = 0; i < block.Transactions!.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    if (tx.To != null)
                    {
                        BeamSyncContext.Description.Value = $"[code prefetch {i}]";
                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        Account? account = stateReader.GetAccount(stateRoot, tx.To);
                        if (account != null)
                        {
                            stateReader.GetCode(account.CodeHash);
                        }
                    }
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(
                    t.IsFaulted ? $"code prefetch failed {t.Exception?.Message}" : $"code prefetch complete - resolved {t.Result}");
            });

            return Task.WhenAll(minerTask, senderTask, codeTask, storageTask);
        }

        private void UnregisterListeners()
        {
            if(_logger.IsDebug) _logger.Debug("Unregistering sync mode listeners.");
            _syncModeSelector.Preparing -= SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing -= SyncModeSelectorOnChanging;
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }

        private bool _isDisposed;
        
        public void Dispose()
        {
            lock (_tokens)
            {
                _isDisposed = true;
                CancelAllBeamSyncTasks();
            }
            
            UnregisterListeners();
        }
    }
}
