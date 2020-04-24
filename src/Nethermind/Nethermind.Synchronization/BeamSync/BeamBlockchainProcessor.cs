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

        private IBlockProcessingQueue _blockchainProcessor;
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
            IBlockProcessingQueue blockchainProcessor,
            ISyncModeSelector syncModeSelector)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _readOnlyBlockTree = new ReadOnlyBlockTree(_blockTree);
            _logger = logManager.GetClassLogger();
            _blockTree.NewBestSuggestedBlock += OnNewBlock;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
        {
            long number = e.Block.Number;
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

        private void OnNewBlock(object sender, BlockEventArgs e)
        {
            Block block = e.Block;
            if (block.IsGenesis || (_syncModeSelector.Current & SyncMode.Full) == SyncMode.Full)
            {
                // TODO: what if we do not want to store receipts?
                _blockchainProcessor.Enqueue(block, ProcessingOptions.StoreReceipts);
            }
            else if ((_syncModeSelector.Current & SyncMode.Beam) == SyncMode.Beam)
            {
                BeamProcess(block);
                long number = block.Number;
                for (int i = 64; i > 6; i--)
                {
                    if (_tokens.TryGetValue(number - i, out CancellationTokenSource token))
                    {
                        token.Cancel();
                    }
                }
            }
        }

        private ConcurrentDictionary<long, CancellationTokenSource> _tokens = new ConcurrentDictionary<long, CancellationTokenSource>();

        private void BeamProcess(Block block)
        {
            CancellationTokenSource cancellationToken = _tokens.GetOrAdd(block.Number, t => new CancellationTokenSource());

            if (block.IsGenesis)
            {
                _blockchainProcessor.Enqueue(block, ProcessingOptions.IgnoreParentNotOnMainChain);
                return;
            }

            // we only want to trace the actual block
            try
            {
                _recoveryStep.RecoverData(block);
                (IBlockchainProcessor processor, IStateReader stateReader) = CreateProcessor(block, new ReadOnlyDbProvider(_readOnlyDbProvider, true), _specProvider, _logManager);

                BlockHeader parentHeader = _readOnlyBlockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (parentHeader != null)
                {
                    PrefetchNew(stateReader, block, parentHeader.StateRoot, parentHeader.Author ?? parentHeader.Beneficiary);
                }

                // Prefetch(block, parentHeader.StateRoot, parentHeader.Author ?? parentHeader.Beneficiary);
                // Prefetch(block, block.StateRoot, block.Author ?? block.Beneficiary);

                if (_logger.IsInfo) _logger.Info($"Now beam processing {block}");
                Block processedBlock = null;
                Task preProcessTask = Task.Run(() =>
                {
                    BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                    BeamSyncContext.Description.Value = $"[preProcess of {block.Hash.ToShortString()}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    BeamSyncContext.Cancelled.Value = cancellationToken.Token;
                    processedBlock = processor.Process(block, ProcessingOptions.ReadOnlyChain | ProcessingOptions.IgnoreParentNotOnMainChain, NullBlockTracer.Instance);
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
                        _blockchainProcessor.Enqueue(block, ProcessingOptions.IgnoreParentNotOnMainChain);
                    }

                    processor.Dispose();
                });
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Block {block.ToString(Block.Format.Short)} failed processing and it will be skipped from beam sync", e);
            }
        }

        private void PrefetchNew(IStateReader stateReader, Block block, Keccak stateRoot, Address miner)
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
            }).ContinueWith(t => { if(_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"{description} prefetch failed {t.Exception.Message}" : $"{description} prefetch complete - resolved {t.Result}"); });

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
            }).ContinueWith(t => { if(_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"tx prefetch failed {t.Exception.Message}" : $"tx prefetch complete - resolved {t.Result}"); });

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
            }).ContinueWith(t => { if(_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"storage prefetch failed {t.Exception.Message}" : $"storage prefetch complete - resolved {t.Result}"); });


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
            }).ContinueWith(t => { if(_logger.IsDebug) _logger.Debug(t.IsFaulted ? $"code prefetch failed {t.Exception.Message}" : $"code prefetch complete - resolved {t.Result}"); });
        }

        public void Dispose()
        {
            _blockTree.NewBestSuggestedBlock -= OnNewBlock;
        }
    }
}