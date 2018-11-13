/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly IBlockValidator _blockValidator;
        private readonly ISnapshotableDb _codeDb;
        private readonly ILogger _logger;
        private readonly IRewardCalculator _rewardCalculator;
        private readonly ISpecProvider _specProvider;
        private readonly ISnapshotableDb _stateDb;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ITransactionPool _transactionPool;
        private readonly IReceiptStorage _receiptStorage;

        public BlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITransactionPool transactionPool,
            IReceiptStorage receiptStorage,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _receiptStorage = receiptStorage;
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        }

        public event EventHandler<BlockProcessedEventArgs> BlockProcessed;
        public event EventHandler<TransactionProcessedEventArgs> TransactionProcessed;

        public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, ProcessingOptions options, ITraceListener traceListener)
        {
            if (suggestedBlocks.Length == 0) return Array.Empty<Block>();

            int stateSnapshot = _stateDb.TakeSnapshot();
            int codeSnapshot = _codeDb.TakeSnapshot();
            Keccak snapshotStateRoot = _stateProvider.StateRoot;

            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                /* discarding the other branch data - chain reorganization */
                _storageProvider.Reset();
                _stateProvider.Reset();
                _stateProvider.StateRoot = branchStateRoot;
            }

            var processedBlocks = new Block[suggestedBlocks.Length];
            try
            {
                for (int i = 0; i < suggestedBlocks.Length; i++)
                {
                    processedBlocks[i] = ProcessOne(suggestedBlocks[i], options, traceListener);
                    if (_logger.IsTrace) _logger.Trace($"Committing trees - state root {_stateProvider.StateRoot}");
                    _stateProvider.CommitTree();
                    _storageProvider.CommitTrees();
                }

                if ((options & ProcessingOptions.ReadOnlyChain) != 0)
                {
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

        private TransactionReceipt[] ProcessTransactions(Block block, ITraceListener traceListener)
        {
            var receipts = new TransactionReceipt[block.Transactions.Length];
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                if (_logger.IsTrace) _logger.Trace($"Processing transaction {i}");
                Transaction currentTx = block.Transactions[i];
                TransactionTrace trace;
                bool shouldTrace = traceListener.ShouldTrace(currentTx.Hash);
                (receipts[i], trace) = _transactionProcessor.Execute(i, currentTx, block.Header, shouldTrace);
                TransactionProcessed?.Invoke(this, new TransactionProcessedEventArgs(receipts[i]));
                if (shouldTrace) traceListener.RecordTrace(currentTx.Hash, trace);
            }

            return receipts;
        }

        private void SetReceiptsRootAndBloom(Block block, TransactionReceipt[] receipts)
        {
            PatriciaTree receiptTree = receipts.Length > 0 ? new PatriciaTree(NullDb.Instance, Keccak.EmptyTreeHash, false) : null;
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _specProvider.GetSpec(block.Header.Number).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                receiptTree?.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            receiptTree?.UpdateRootHash();

            block.Header.ReceiptsRoot = receiptTree?.RootHash ?? PatriciaTree.EmptyTreeHash;
            block.Header.Bloom = receipts.Length > 0 ? TransactionProcessor.BuildBloom(receipts) : Bloom.Empty;
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

        private Block ProcessOne(Block suggestedBlock, ProcessingOptions options, ITraceListener traceListener)
        {
            if (suggestedBlock.IsGenesis) return suggestedBlock;

            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
            {
                if (_logger.IsInfo) _logger.Info("Applying DAO transition");
                ApplyDaoTransition();
            }

            Block block = PrepareBlockForProcessing(suggestedBlock);
            var receipts = ProcessTransactions(block, traceListener);
            SetReceiptsRootAndBloom(block, receipts);
            ApplyMinerRewards(block);
            _stateProvider.Commit(_specProvider.GetSpec(block.Number));

            block.Header.StateRoot = _stateProvider.StateRoot;
            block.Header.Hash = BlockHeader.CalculateHash(block.Header);
            if ((options & ProcessingOptions.NoValidation) == 0 && !_blockValidator.ValidateProcessedBlock(block, suggestedBlock))
            {
                if (_logger.IsError) _logger.Error($"Processed block is not valid {suggestedBlock.ToString(Block.Format.HashAndNumber)}");
                throw new InvalidBlockException($"{suggestedBlock.ToString(Block.Format.HashAndNumber)}");
            }

            if ((options & ProcessingOptions.StoreReceipts) != 0)
            {
                StoreTxReceipts(block, receipts);
            }

            BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(block));
            return block;
        }

        private void StoreTxReceipts(Block block, TransactionReceipt[] receipts)
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                receipts[i].BlockHash = block.Hash;
                _receiptStorage.Add(receipts[i]);
                _transactionPool.RemoveTransaction(receipts[i].TransactionHash);
            }
        }

        private Block PrepareBlockForProcessing(Block suggestedBlock)
        {
            if (_logger.IsTrace) _logger.Trace($"{suggestedBlock.Header.ToString(BlockHeader.Format.Full)}");

            BlockHeader s = suggestedBlock.Header;
            BlockHeader header = new BlockHeader(s.ParentHash, s.OmmersHash, s.Beneficiary, s.Difficulty, s.Number, s.GasLimit, s.Timestamp, s.ExtraData);
            Block processedBlock = new Block(header, suggestedBlock.Transactions, suggestedBlock.Ommers);
            header.Author = suggestedBlock.Header.Author;
            header.Hash = s.Hash;
            header.MixHash = s.MixHash;
            header.Nonce = s.Nonce;
            header.TransactionsRoot = suggestedBlock.TransactionsRoot;
            return processedBlock;
        }

        private void ApplyMinerRewards(Block block)
        {
            if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
            var rewards = _rewardCalculator.CalculateRewards(block);
            for (int i = 0; i < rewards.Length; i++)
            {
                BlockReward reward = rewards[i];
                ApplyMinerReward(block, reward);
            }
        }

        private void ApplyMinerReward(Block block, BlockReward reward)
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
            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.AddToBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.SubtractFromBalance(daoAccount, balance, Dao.Instance);
            }
        }
    }
}