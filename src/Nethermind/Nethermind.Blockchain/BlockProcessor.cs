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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Proofs;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly ILogger _logger;
        private readonly ITxPool _txPool;
        private readonly ISnapshotableDb _codeDb;
        private readonly ISnapshotableDb _stateDb;
        private readonly ISpecProvider _specProvider;
        private readonly IStateProvider _stateProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly IStorageProvider _storageProvider;
        private readonly IRewardCalculator _rewardCalculator;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IAdditionalBlockProcessor _additionalBlockProcessor;

        public BlockProcessor(ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITxPool txPool,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IEnumerable<IAdditionalBlockProcessor> additionalBlockProcessors = null)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _receiptsTracer = new BlockReceiptsTracer();

            if (additionalBlockProcessors != null)
            {
                var additionalBlockProcessorsArray = additionalBlockProcessors.ToArray();
                if (additionalBlockProcessorsArray.Length > 0)
                {
                    _additionalBlockProcessor = additionalBlockProcessorsArray.Length == 1
                        ? additionalBlockProcessorsArray[0]
                        : new CompositeAdditionalBlockProcessor(additionalBlockProcessorsArray);
                }
            }
        }

        public event EventHandler<BlockProcessedEventArgs> BlockProcessed;
        public event EventHandler<TxProcessedEventArgs> TransactionProcessed;

        public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer)
        {
            if(_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlocks[0].Number} from state root: {branchStateRoot}");
            
            if (suggestedBlocks.Length == 0) return Array.Empty<Block>();

            int stateSnapshot = _stateDb.TakeSnapshot();
            int codeSnapshot = _codeDb.TakeSnapshot();
            if (stateSnapshot != -1 || codeSnapshot != -1)
            {
                if(_logger.IsError) _logger.Error($"Uncommitted state ({stateSnapshot}, {codeSnapshot}) when processing from a branch root {branchStateRoot} starting with block {suggestedBlocks[0].ToString(Block.Format.Short)}");
            }
            
            Keccak snapshotStateRoot = _stateProvider.StateRoot;

            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                /* discarding the other branch data - chain reorganization */
                Metrics.Reorganizations++;
                _storageProvider.Reset();
                _stateProvider.Reset();
                _stateProvider.StateRoot = branchStateRoot;
            }

            var processedBlocks = new Block[suggestedBlocks.Length];
            try
            {
                for (int i = 0; i < suggestedBlocks.Length; i++)
                {
                    processedBlocks[i] = ProcessOne(suggestedBlocks[i], options, blockTracer);
                    if (_logger.IsTrace) _logger.Trace($"Committing trees - state root {_stateProvider.StateRoot}");
                    _stateProvider.CommitTree();
                    _storageProvider.CommitTrees();
                }

                if ((options & ProcessingOptions.ReadOnlyChain) != 0)
                {
                    _receiptsTracer.BeforeRestore(_stateProvider);
                    Restore(stateSnapshot, codeSnapshot, snapshotStateRoot);
                }
                else
                {
                    _stateDb.Commit();
                    _codeDb.Commit();
                }

                return processedBlocks;
            }
            catch (InvalidBlockException)
            {
                Restore(stateSnapshot, codeSnapshot, snapshotStateRoot);
                throw;
            }
        }

        private BlockReceiptsTracer _receiptsTracer;
        
        private TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer)
        {
            _receiptsTracer.SetOtherTracer(blockTracer);
            _receiptsTracer.StartNewBlockTrace(block);   

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                if (_logger.IsTrace) _logger.Trace($"Processing transaction {i}");
                Transaction currentTx = block.Transactions[i];
                _receiptsTracer.StartNewTxTrace(currentTx.Hash);
                _transactionProcessor.Execute(currentTx, block.Header, _receiptsTracer);
                _receiptsTracer.EndTxTrace();

                if ((processingOptions & ProcessingOptions.ReadOnlyChain) == 0)
                {
                    TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(_receiptsTracer.TxReceipts[i]));
                }
            }

            return _receiptsTracer.TxReceipts;
        }

        private void SetReceiptsRoot(Block block, TxReceipt[] txReceipts)
        {
            ReceiptTrie receiptTrie = new ReceiptTrie(block.Number, _specProvider, txReceipts);
            block.Header.ReceiptsRoot = receiptTrie.RootHash;
        }

        private void Restore(int stateSnapshot, int codeSnapshot, Keccak snapshotStateRoot)
        {
            if (_logger.IsTrace) _logger.Trace($"Reverting blocks {_stateProvider.StateRoot}");
            _stateDb.Restore(stateSnapshot);
            _codeDb.Restore(codeSnapshot);
            _storageProvider.Reset();
            _stateProvider.Reset();
            _stateProvider.StateRoot = snapshotStateRoot;
            if (_logger.IsTrace) _logger.Trace($"Reverted blocks {_stateProvider.StateRoot}");
        }

        private Block ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer)
        {
            Block block;
            if (suggestedBlock.IsGenesis)
            {
                block = suggestedBlock;
            }
            else
            {
                if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
                {
                    if (_logger.IsInfo) _logger.Info("Applying DAO transition");
                    ApplyDaoTransition();
                }

                block = PrepareBlockForProcessing(suggestedBlock);
                _additionalBlockProcessor?.PreProcess(block, options);
                
                var receipts = ProcessTransactions(block, options, blockTracer);
                SetReceiptsRoot(block, receipts);
                ApplyMinerRewards(block, blockTracer);
                
                _stateProvider.Commit(_specProvider.GetSpec(block.Number));
                block.Header.StateRoot = _stateProvider.StateRoot;
                block.Header.Hash = block.Header.CalculateHash();
                
                _additionalBlockProcessor?.PostProcess(block, receipts, options);

                if ((options & ProcessingOptions.NoValidation) == 0 && !_blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock))
                {
                    if (_logger.IsError) _logger.Error($"Processed block is not valid {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}");
                    // if (_logger.IsError) _logger.Error($"State: {_stateProvider.DumpState()}");
                    throw new InvalidBlockException(suggestedBlock.Hash);
                }

                if ((options & ProcessingOptions.StoreReceipts) != 0)
                {
                    StoreTxReceipts(block, receipts);
                }
            }

            if ((options & ProcessingOptions.ReadOnlyChain) == 0)
            {
                BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(block));
            }

            return block;
        }

        private void StoreTxReceipts(Block block, TxReceipt[] txReceipts)
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                txReceipts[i].BlockHash = block.Hash;
                _receiptStorage.Add(txReceipts[i], true);
                _txPool.RemoveTransaction(txReceipts[i].TxHash, block.Number);
            }
        }
        
        private Block PrepareBlockForProcessing(Block suggestedBlock)
        {
            if (_logger.IsTrace) _logger.Trace($"{suggestedBlock.Header.ToString(BlockHeader.Format.Full)}");

            BlockHeader bh = suggestedBlock.Header;
            BlockHeader header = new BlockHeader(
                bh.ParentHash,
                bh.OmmersHash,
                bh.Beneficiary,
                bh.Difficulty,
                bh.Number,
                bh.GasLimit,
                bh.Timestamp,
                bh.ExtraData)
            {
                Bloom = Bloom.Empty,
                Author = bh.Author,
                Hash = bh.Hash,
                MixHash = bh.MixHash,
                Nonce = bh.Nonce,
                TxRoot = bh.TxRoot,
                TotalDifficulty = bh.TotalDifficulty,
                AuRaStep = bh.AuRaStep,
                AuRaSignature = bh.AuRaSignature                
            };
            return new Block(header, suggestedBlock.Transactions, suggestedBlock.Ommers);;
        }

        private void ApplyMinerRewards(Block block, IBlockTracer tracer)
        {
            if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
            var rewards = _rewardCalculator.CalculateRewards(block);
            for (int i = 0; i < rewards.Length; i++)
            {
                BlockReward reward = rewards[i];

                ITxTracer txTracer = null;
                if (tracer.IsTracingRewards)
                {
                    txTracer = tracer.StartNewTxTrace(null);
                }

                ApplyMinerReward(block, reward, tracer.IsTracingRewards ? tracer : NullBlockTracer.Instance);
                
                if (tracer.IsTracingRewards)
                {
                    tracer.EndTxTrace();
                    tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), (UInt256) reward.Value);
                    if (txTracer?.IsTracingState ?? false)
                    {
                        _stateProvider.Commit(_specProvider.GetSpec(block.Number), txTracer);
                    }
                }
            }
        }

        private void ApplyMinerReward(Block block, BlockReward reward, IBlockTracer tracer)
        {
            if (_logger.IsTrace) _logger.Trace($"  {(decimal) reward.Value / (decimal) Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

            if (!_stateProvider.AccountExists(reward.Address))
            {
                _stateProvider.CreateAccount(reward.Address, (UInt256) reward.Value);
            }
            else
            {
                _stateProvider.AddToBalance(reward.Address, (UInt256) reward.Value, _specProvider.GetSpec(block.Number));
            }
        }

        private void ApplyDaoTransition()
        {
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            if (!_stateProvider.AccountExists(withdrawAccount))
            {
                _stateProvider.CreateAccount(withdrawAccount, 0);
            }

            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.AddToBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.SubtractFromBalance(daoAccount, balance, Dao.Instance);
            }
        }
        
        private class CompositeAdditionalBlockProcessor : IAdditionalBlockProcessor
        {
            private readonly IAdditionalBlockProcessor[] _additionalBlockProcessors;

            public CompositeAdditionalBlockProcessor(params IAdditionalBlockProcessor[] additionalBlockProcessors)
            {
                _additionalBlockProcessors = additionalBlockProcessors ?? throw new ArgumentNullException(nameof(additionalBlockProcessors));
            }
            
            public void PreProcess(Block block, ProcessingOptions options)
            {
                for (int i = 0; i < _additionalBlockProcessors.Length; i++)
                {
                    _additionalBlockProcessors[i].PreProcess(block, options);
                }
            }

            public void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options)
            {
                for (int i = 0; i < _additionalBlockProcessors.Length; i++)
                {
                    _additionalBlockProcessors[i].PostProcess(block, receipts, options);
                }
            }
        }
    }
}