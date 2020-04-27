//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
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

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamBlockchainProcessor : IDisposable
    {
        private readonly IReadOnlyDbProvider _readOnlyDbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly ILogger _logger;

        private IBlockProcessingQueue _standardProcessorQueue;
        private readonly IBlockchainProcessor _processor;
        private readonly ISyncModeSelector _syncModeSelector;
        private ReadOnlyBlockTree _readOnlyBlockTree;
        private IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;

        public BeamBlockchainProcessor(
            IReadOnlyDbProvider readOnlyDbProvider,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockValidator blockValidator,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IBlockProcessingQueue processingQueue,
            IBlockchainProcessor processor,
            ISyncModeSelector syncModeSelector)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _standardProcessorQueue = processingQueue ?? throw new ArgumentNullException(nameof(processingQueue));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _readOnlyBlockTree = new ReadOnlyBlockTree(_blockTree);
            _logger = logManager.GetClassLogger();
            _blockTree.NewBestSuggestedBlock += OnNewBlock;
            _blockAction = BeamProcess;

            _syncModeSelector.Preparing += SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing += SyncModeSelectorOnChanging;
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            _blockAction = EnqueueForStandardProcessing;
        }

        private Action<Block> _blockAction;

        private Queue<Block> _shelvedBlocks = new Queue<Block>();

        private void EnqueueForStandardProcessing(Block block)
        {
            while (_shelvedBlocks.TryDequeue(out Block shelvedBlock))
            {
                _standardProcessorQueue.Enqueue(shelvedBlock, ProcessingOptions.StoreReceipts);
            }

            _standardProcessorQueue.Enqueue(block, ProcessingOptions.StoreReceipts);
        }

        private void Shelve(Block block)
        {
            _shelvedBlocks.Enqueue(block);
        }

        private object _transitionLock = new object();

        private bool _isAfterBeam;

        private void SyncModeSelectorOnPreparing(object sender, SyncModeChangedEventArgs e)
        {
            if ((e.Current & SyncMode.Full) == SyncMode.Full)
            {
                lock (_transitionLock)
                {
                    if (_isAfterBeam)
                    {
                        return;
                    }

                    _isAfterBeam = true;
                }
                
                lock (_beamProcessTasks)
                {
                    _blockAction = Shelve;
                    
                    foreach (KeyValuePair<long, CancellationTokenSource> cancellationTokenSource in _tokens)
                    {
                        cancellationTokenSource.Value.Cancel();
                    }

                    Task.WhenAll(_beamProcessTasks).Wait(); // sync mode selector is waiting for beam syncing blocks to stop
                }
            }
        }

        private void SyncModeSelectorOnChanging(object sender, SyncModeChangedEventArgs e)
        {
        }

        /// <summary>
        /// Whenever we finish beam syncing one of the blocks we cancel all previous ones
        /// and move our processing power to the future
        /// </summary>
        /// <param name="number">Number of the block that we have just processed</param>
        private void CancelPreviousBeamSyncingBlocks(long number)
        {
            for (int i = 64; i > 0; i--)
            {
                if (_tokens.TryGetValue(number - i, out CancellationTokenSource token))
                {
                    token.Cancel();
                }
            }
        }

        private (IBlockchainProcessor, IStateReader) CreateProcessor(Block block, IReadOnlyDbProvider readOnlyDbProvider, ISpecProvider specProvider, ILogManager logManager)
        {
            ReadOnlyTxProcessingEnv txEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, _readOnlyBlockTree, specProvider, logManager);
            ReadOnlyChainProcessingEnv env = new ReadOnlyChainProcessingEnv(txEnv, _blockValidator, _recoveryStep, _rewardCalculatorSource.Get(txEnv.TransactionProcessor), NullReceiptStorage.Instance, _readOnlyDbProvider, specProvider, logManager);
            env.BlockProcessor.TransactionProcessed += (sender, args) =>
            {
                if (_logger.IsInfo) _logger.Info($"Processed tx {args.Index}/{block.Transactions.Length} of {block.Number}");
            };

            return (env.ChainProcessor, txEnv.StateReader);
        }

        private ConcurrentBag<Task> _beamProcessTasks = new ConcurrentBag<Task>();

        private void OnNewBlock(object sender, BlockEventArgs e)
        {
            Block block = e.Block;
            if (block.IsGenesis)
            {
                EnqueueForStandardProcessing(block);
            }
            
            lock (_beamProcessTasks)
            {
                if (_isAfterBeam)
                {
                    // TODO: what if we do not want to store receipts?
                    EnqueueForStandardProcessing(block);
                }
                else // beam sync
                {
                    BeamProcess(block);
     
                }
            }
        }

        /// <summary>
        /// Tokens really should be by hash or hash sets by number
        /// </summary>
        private ConcurrentDictionary<long, CancellationTokenSource> _tokens = new ConcurrentDictionary<long, CancellationTokenSource>();

        private void BeamProcess(Block block)
        {
            CancellationTokenSource cancellationToken = _tokens.GetOrAdd(block.Number, t => new CancellationTokenSource());

            Task beamProcessingTask = Task.CompletedTask;
            Task prefetchTasks = Task.CompletedTask;

            // we only want to trace the actual block
            try
            {
                _recoveryStep.RecoverData(block);
                (IBlockchainProcessor beamProcessor, IStateReader stateReader) = CreateProcessor(block, new ReadOnlyDbProvider(_readOnlyDbProvider, true), _specProvider, _logManager);

                BlockHeader parentHeader = _readOnlyBlockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (parentHeader != null)
                {
                    prefetchTasks = PrefetchNew(stateReader, block, parentHeader.StateRoot, parentHeader.Author ?? parentHeader.Beneficiary);
                }

                if (_logger.IsInfo) _logger.Info($"Now beam processing {block}");
                Block processedBlock = null;
                beamProcessingTask = Task.Run(() =>
                {
                    BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                    BeamSyncContext.Description.Value = $"[preProcess of {block.Hash.ToShortString()}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                    processedBlock = beamProcessor.Process(block, ProcessingOptions.Beam, NullBlockTracer.Instance);
                    if (processedBlock == null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block {block.ToString(Block.Format.Short)} skipped in beam sync");
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsInfo) _logger.Info($"Stopped processing block {block} | {t.Exception?.Flatten().InnerException?.Message}");
                        if (_logger.IsDebug) _logger.Debug($"Details of beam sync failure {block} | {t.Exception}");

                        return;
                    }

                    if (processedBlock != null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Enqueuing for standard processing {block}");
                        // at this stage we are sure to have all the state available
                        CancelPreviousBeamSyncingBlocks(processedBlock.Number);
                        _processor.Process(block, ProcessingOptions.Beam, NullBlockTracer.Instance);
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
            for (int i = 64; i > 6; i--)
            {
                if (_tokens.TryGetValue(number - i, out CancellationTokenSource token))
                {
                    token.Cancel();
                }
            }
        }

        private Task PrefetchNew(IStateReader stateReader, Block block, Keccak stateRoot, Address miner)
        {
            CancellationTokenSource cancellationToken = _tokens.GetOrAdd(block.Number, t => new CancellationTokenSource());
            string description = $"[miner {miner}]";
            Task minerTask = Task<int>.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                BeamSyncContext.Description.Value = description;
                BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                stateReader.GetAccount(stateRoot, miner);
                BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"{description} prefetch failed {t.Exception.Message}" : $"{description} prefetch complete - resolved {t.Result}");
            });

            Task senderTask = Task<int>.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    BeamSyncContext.Description.Value = $"[tx prefetch {i}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                    // _logger.Info($"Resolved sender of {block.Number}.{i}");
                    stateReader.GetAccount(stateRoot, tx.To);
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"tx prefetch failed {t.Exception.Message}" : $"tx prefetch complete - resolved {t.Result}");
            });

            Task storageTask = Task<int>.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty ?? 0;
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    if (tx.To != null)
                    {
                        BeamSyncContext.Description.Value = $"[storage prefetch {i}]";
                        // _logger.Info($"Resolved storage of target of {block.Number}.{i}");
                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                        stateReader.GetStorageRoot(stateRoot, tx.To);
                    }
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"storage prefetch failed {t.Exception.Message}" : $"storage prefetch complete - resolved {t.Result}");
            });


            Task codeTask = Task<int>.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction tx = block.Transactions[i];
                    if (tx.To != null)
                    {
                        BeamSyncContext.Description.Value = $"[code prefetch {i}]";
                        // _logger.Info($"Resolved code of target of {block.Number}.{i}");
                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                        stateReader.GetCode(stateRoot, tx.SenderAddress);
                        return BeamSyncContext.ResolvedInContext.Value;
                    }
                }

                return BeamSyncContext.ResolvedInContext.Value;
            }).ContinueWith(t =>
            {
                if (_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"code prefetch failed {t.Exception.Message}" : $"code prefetch complete - resolved {t.Result}");
            });

            return Task.WhenAll(minerTask, senderTask, codeTask, storageTask);
        }

        public void Dispose()
        {
        }
    }
}