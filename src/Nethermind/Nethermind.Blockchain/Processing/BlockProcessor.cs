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
using System.Numerics;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Processing
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

        /// <summary>
        /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
        /// to any block-specific tracers.
        /// </summary>
        private BlockReceiptsTracer _receiptsTracer;

        public BlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITxPool txPool,
            IReceiptStorage receiptStorage,
            ILogManager logManager)
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
        }

        public event EventHandler<BlockProcessedEventArgs> BlockProcessed;

        public event EventHandler<TxProcessedEventArgs> TransactionProcessed;

        public Block[] Process(Keccak newBranchStateRoot, List<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer)
        {
            if (suggestedBlocks.Count == 0) return Array.Empty<Block>();

            /* We need to save the snapshot state root before reorganization in case the new branch has invalid blocks.
               In case of invalid blocks on the new branch we will discard the entire branch and come back to 
               the previous head state.*/
            Keccak previousBranchStateRoot = CreateCheckpoint();
            InitBranch(newBranchStateRoot);

            bool readOnly = (options & ProcessingOptions.ReadOnlyChain) != 0;
            Block[] processedBlocks = new Block[suggestedBlocks.Count];
            try
            {
                for (int i = 0; i < suggestedBlocks.Count; i++)
                {
                    processedBlocks[i] = ProcessOne(suggestedBlocks[i], options, blockTracer);

                    // be cautious here as AuRa depends on processing
                    PreCommitBlock(newBranchStateRoot); // only needed if we plan to read state root?
                    if (!readOnly)
                    {
                        BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(processedBlocks[i]));
                    }
                }

                if (readOnly)
                {
                    RestoreBranch(previousBranchStateRoot);
                }
                else
                {
                    CommitBranch();
                }

                return processedBlocks;
            }
            catch (Exception) // try to restore for all cost
            {
                RestoreBranch(previousBranchStateRoot);
                throw;
            }
        }

        private void InitBranch(Keccak branchStateRoot)
        {
            /* Please note that we do not reset the state if branch state root is null.
               That said, I do not remember in what cases we receive null here.*/
            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                /* Discarding the other branch data - chain reorganization.
                   We cannot use cached values any more because they may have been written
                   by blocks that are being reorganized out.*/
                Metrics.Reorganizations++;
                _storageProvider.Reset();
                _stateProvider.Reset();
                _stateProvider.StateRoot = branchStateRoot;
            }
        }

        private Keccak CreateCheckpoint()
        {
            Keccak currentBranchStateRoot = _stateProvider.StateRoot;

            /* Below is a non-critical assertion that nonetheless should be addressed when it happens. */
            if (_stateDb.HasUncommittedChanges || _codeDb.HasUncommittedChanges)
            {
                if (_logger.IsError) _logger.Error($"Uncommitted state when processing from a branch root {currentBranchStateRoot}.");
            }

            return currentBranchStateRoot;
        }

        private void PreCommitBlock(Keccak newBranchStateRoot)
        {
            if (_logger.IsTrace) _logger.Trace($"Committing the branch - {newBranchStateRoot} | {_stateProvider.StateRoot}");
            _stateProvider.CommitTree();
            _storageProvider.CommitTrees();
        }
        
        private void CommitBranch()
        {
            _stateDb.Commit();
            _codeDb.Commit();
        }

        private void RestoreBranch(Keccak branchingPointStateRoot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring the branch checkpoint - {branchingPointStateRoot}");
            _stateDb.Restore(ISnapshotableDb.NoChangesCheckpoint);
            _codeDb.Restore(ISnapshotableDb.NoChangesCheckpoint);
            _storageProvider.Reset();
            _stateProvider.Reset();
            _stateProvider.StateRoot = branchingPointStateRoot;
            if (_logger.IsTrace) _logger.Trace($"Restored the branch checkpoint - {branchingPointStateRoot} | {_stateProvider.StateRoot}");
        }

        private TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer)
        {
            _receiptsTracer.SetOtherTracer(blockTracer);
            _receiptsTracer.StartNewBlockTrace(block);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                if ((processingOptions & ProcessingOptions.DoNotVerifyNonce) != 0)
                {
                    currentTx.Nonce = _stateProvider.GetNonce(currentTx.SenderAddress);
                }

                _receiptsTracer.StartNewTxTrace(currentTx.Hash);
                _transactionProcessor.Execute(currentTx, block.Header, _receiptsTracer);
                _receiptsTracer.EndTxTrace();

                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, currentTx, _receiptsTracer.TxReceipts[i]));
            }

            return _receiptsTracer.TxReceipts;
        }

        private Block ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer)
        {
            ApplyDaoTransition(suggestedBlock);
            Block block = PrepareBlockForProcessing(suggestedBlock);
            TxReceipt[] receipts = ProcessBlock(block, blockTracer, options);
            ValidateProcessedBlock(suggestedBlock, options, block, receipts);
            if ((options & ProcessingOptions.StoreReceipts) != 0)
            {
                StoreTxReceipts(block, receipts);
            }

            return block;
        }

        private void ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block, TxReceipt[] receipts)
        {
            if ((options & ProcessingOptions.NoValidation) == 0 && !_blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock))
            {
                if (_logger.IsError) _logger.Error($"Processed block is not valid {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}");
                throw new InvalidBlockException(suggestedBlock.Hash);
            }
        }

        protected virtual TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            TxReceipt[] receipts = ProcessTransactions(block, options, blockTracer);
            SetReceiptsRoot(block, receipts);
            ApplyMinerRewards(block, blockTracer);

            _stateProvider.Commit(_specProvider.GetSpec(block.Number));
            _stateProvider.RecalculateStateRoot();
            block.Header.StateRoot = _stateProvider.StateRoot;
            block.Header.Hash = block.Header.CalculateHash();

            return receipts;
        }

        private void SetReceiptsRoot(Block block, TxReceipt[] txReceipts)
        {
            ReceiptTrie receiptTrie = new ReceiptTrie(block.Number, _specProvider, txReceipts);
            block.Header.ReceiptsRoot = receiptTrie.RootHash;
        }

        private void StoreTxReceipts(Block block, TxReceipt[] txReceipts)
        {
            _receiptStorage.Insert(block, txReceipts);
            for (int i = 0; i < block.Transactions.Length; i++)
            {
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

            return new Block(header, suggestedBlock.Transactions, suggestedBlock.Ommers);
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
                    // we need this tracer to be able to track any potential miner account creation
                    txTracer = tracer.StartNewTxTrace(null);
                }

                ApplyMinerReward(block, reward);

                if (tracer.IsTracingRewards)
                {
                    tracer.EndTxTrace();
                    tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
                    if (txTracer?.IsTracingState ?? false)
                    {
                        _stateProvider.Commit(_specProvider.GetSpec(block.Number), txTracer);
                    }
                }
            }
        }

        private void ApplyMinerReward(Block block, BlockReward reward)
        {
            if (_logger.IsTrace) _logger.Trace($"  {(BigInteger) reward.Value / (BigInteger) Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

            if (!_stateProvider.AccountExists(reward.Address))
            {
                _stateProvider.CreateAccount(reward.Address, reward.Value);
            }
            else
            {
                _stateProvider.AddToBalance(reward.Address, reward.Value, _specProvider.GetSpec(block.Number));
            }
        }

        private void ApplyDaoTransition(Block block)
        {
            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == block.Header.Number)
            {
                if (_logger.IsInfo) _logger.Info("Applying the DAO transition");
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
        }
    }
}
