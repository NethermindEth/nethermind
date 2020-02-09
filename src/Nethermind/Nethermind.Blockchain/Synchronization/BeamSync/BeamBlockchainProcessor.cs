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
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.BeamSync;

namespace Nethermind.Blockchain.Synchronization.BeamSync
{
    public class BeamBlockchainProcessor : IDisposable
    {
        private readonly IReadOnlyDbProvider _readOnlyDbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly ILogger _logger;

        private IBlockProcessingQueue _blockchainProcessor;
        private IBlockchainProcessor _oneTimeProcessor;
        private IStateReader _stateReader;
        private ReadOnlyBlockTree _readOnlyBlockTree;
        private IBlockTree _blockTree;

        public BeamBlockchainProcessor(
            IReadOnlyDbProvider readOnlyDbProvider,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockValidator blockValidator,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IBlockProcessingQueue blockchainProcessor)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockTree.NewBestSuggestedBlock += OnNewBlock;
            _readOnlyBlockTree = new ReadOnlyBlockTree(_blockTree);

            ReadOnlyTxProcessingEnv txEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, _readOnlyBlockTree, specProvider, logManager);
            _stateReader = txEnv.StateReader;

            ReadOnlyChainProcessingEnv env = new ReadOnlyChainProcessingEnv(txEnv, _blockValidator, _recoveryStep, _rewardCalculatorSource.Get(txEnv.TransactionProcessor), NullReceiptStorage.Instance, _readOnlyDbProvider, specProvider, logManager);
            _oneTimeProcessor = env.ChainProcessor;
            _logger = logManager.GetClassLogger();
        }

        private void OnNewBlock(object sender, BlockEventArgs e)
        {
            Process(e.Block, ProcessingOptions.None);
        }
        
        private void Process(Block block, ProcessingOptions options)
        {
            if (block.IsGenesis)
            {
                _blockchainProcessor.Enqueue(block, ProcessingOptions.None);
                return;
            }
            
            // we only want to trace the actual block
            try
            {
                BlockHeader parentHeader = _readOnlyBlockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                Prefetch(block, parentHeader.StateRoot, parentHeader.Author ?? parentHeader.Beneficiary);
                Prefetch(block, block.StateRoot, block.Author ?? block.Beneficiary);

                if(_logger.IsInfo) _logger.Info($"Now beam processing {block}");
                Block processedBlock = null;
                Task preProcessTask = Task.Run(() =>
                {
                    BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                    BeamSyncContext.Description.Value = $"[preProcess of {block.Hash.ToShortString()}]";
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    processedBlock = _oneTimeProcessor.Process(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);
                    if (processedBlock == null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Block {block.ToString(Block.Format.Short)} skipped in beam sync");
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Warn($"Failed to beam process {block}");
                        return;
                    }
                    
                    if (processedBlock != null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Enqueuing for standard processing {block}");
                        // at this stage we are sure to have all the state available
                        _blockchainProcessor.Enqueue(block, options);
                    }
                });
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Block {block.ToString(Block.Format.Short)} failed processing and it will be skipped from beam sync", e);
            }
        }

        private void Prefetch(Block block, Keccak stateRoot, Address miner)
        {
            string description = $"[miner {miner}]";
            Task minerTask = Task.Run(() =>
            {
                BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                BeamSyncContext.Description.Value = description;
                BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                _stateReader.GetAccount(stateRoot, miner);
            }).ContinueWith(t =>
            {
                _logger.Info(t.IsFaulted ? $"{description} prefetch failed {t.Exception.Message}" : $"{description} prefetch complete");
            });

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction tx = block.Transactions[i];
                _recoveryStep.RecoverData(block);
                int txIndex = i;
                string descriptionTx = $"[sender of tx {txIndex} of {block.Hash.ToShortString()}]";
                Task senderTask = Task.Run(() =>
                {
                    BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                    BeamSyncContext.Description.Value = descriptionTx;
                    BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                    _stateReader.GetAccount(stateRoot, tx.To);
                }).ContinueWith(t =>
                {
                    _logger.Info(t.IsFaulted ? $"{descriptionTx} prefetch failed {t.Exception.Message}" : $"{descriptionTx} prefetch complete");
                });

                string descriptionCode = $"[code of tx {txIndex} of {block.Hash.ToShortString()}]";
                if (tx.To != null)
                {
                    Task codeTask = Task.Run(() =>
                    {
                        BeamSyncContext.MinimumDifficulty.Value = block.TotalDifficulty.Value;
                        BeamSyncContext.Description.Value = descriptionCode;
                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        _stateReader.GetCode(stateRoot, tx.SenderAddress);
                    }).ContinueWith(t =>
                    {
                        _logger.Info(t.IsFaulted ? $"{descriptionCode} prefetch failed {t.Exception.Message}" : $"{descriptionCode} prefetch complete");
                    });
                }   
            }
        }

        public void Dispose()
        {
            _blockTree.NewBestSuggestedBlock -= OnNewBlock;
        }
    }
}