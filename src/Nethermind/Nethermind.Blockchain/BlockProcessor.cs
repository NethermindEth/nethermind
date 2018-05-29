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
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IDbProvider _dbProvider;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly ITransactionStore _transactionStore;
        private readonly IRewardCalculator _rewardCalculator;

        public BlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            IDbProvider dbProvider,
            IStateProvider stateProvider,
            IStorageProvider storageProvider, ITransactionStore transactionStore, ILogger logger = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _specProvider = specProvider;
            _blockValidator = blockValidator;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _transactionStore = transactionStore;
            _rewardCalculator = rewardCalculator;
            _transactionProcessor = transactionProcessor;
            _dbProvider = dbProvider;
        }

        private readonly IBlockValidator _blockValidator;

        private void ProcessTransactions(Block block, Transaction[] transactions)
        {
            TransactionReceipt[] receipts = new TransactionReceipt[transactions.Length];
            for (int i = 0; i < transactions.Length; i++)
            {
                var transaction = transactions[i];
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Processing transaction {i}");
                }

                // TODO: setup a DB for this
//                _transactionStore.AddTransaction(transaction);
                TransactionReceipt receipt = _transactionProcessor.Execute(transaction, block.Header);
                if (transaction.Hash == null)
                {
                    throw new InvalidOperationException("Transaction's hash is null when processing");
                }

                // TODO: setup a DB for this
//                _transactionStore.AddTransactionReceipt(transaction.Hash, receipt, block.Hash);
                receipts[i] = receipt;
            }

            SetReceipts(block, receipts);
        }

        private void SetReceipts(Block block, TransactionReceipt[] receipts)
        {
            PatriciaTree receiptTree = receipts.Length > 0 ? new PatriciaTree(NullDb.Instance) : null;
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _specProvider.GetSpec(block.Header.Number).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                receiptTree?.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            receiptTree?.UpdateRootHash();

            block.Receipts = receipts;
            block.Header.ReceiptsRoot = receiptTree?.RootHash ?? PatriciaTree.EmptyTreeHash;
            block.Header.Bloom = receipts.Length > 0 ? TransactionProcessor.BuildBloom(receipts.SelectMany(r => r.Logs).ToArray()) : Bloom.Empty; // TODO not tested anywhere at the time of writing
        }

        private Keccak GetTransactionsRoot(Transaction[] transactions)
        {
            if (transactions.Length == 0)
            {
                return PatriciaTree.EmptyTreeHash;
            }
            
            PatriciaTree txTree = new PatriciaTree();
            for (int i = 0; i < transactions.Length; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                txTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            txTree.UpdateRootHash();
            return txTree.RootHash;
        }

        public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, bool tryOnly)
        {
            int dbSnapshot = _dbProvider.TakeSnapshot();
            Keccak snapshotStateRoot = _stateProvider.StateRoot;

            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                // discarding one of the branches
                _storageProvider.ClearCaches();
                _stateProvider.Reset();
                _stateProvider.StateRoot = branchStateRoot;
            }

            Block[] processedBlocks = new Block[suggestedBlocks.Length];
            try
            {
                for (int i = 0; i < suggestedBlocks.Length; i++)
                {
                    processedBlocks[i] = ProcessOne(suggestedBlocks[i], tryOnly);
                }

                if (tryOnly)
                {
                    // TODO: this is some rapid and bad implementation, need to rmeove the clear caches approach
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"REVERTING BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                    }

                    _dbProvider.Restore(dbSnapshot);
                    _storageProvider.ClearCaches();
                    _stateProvider.Reset();
                    _stateProvider.StateRoot = snapshotStateRoot;

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"REVERTED BLOCKS (JUST VALIDATED FOR MINING) - STATE ROOT {_stateProvider.StateRoot}");
                    }
                }

                return processedBlocks;
            }
            catch (InvalidBlockException) // TODO: which exception to catch here?
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"REVERTING BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                }

                _dbProvider.Restore(dbSnapshot);
                _storageProvider.ClearCaches();
                _stateProvider.Reset();
                _stateProvider.StateRoot = snapshotStateRoot;

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"REVERTED BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                }

                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"THROWING INVALID BLOCK");
                }

                throw;
            }
        }

        private void ApplyDaoTransition()
        {
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            _stateProvider.CreateAccount(withdrawAccount, BigInteger.Zero);

            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                BigInteger balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.UpdateBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.UpdateBalance(daoAccount, -balance, Dao.Instance);
            }
        }

        private Block ProcessOne(Block suggestedBlock, bool tryOnly) // TODO: refactor
        {
            if (suggestedBlock.IsGenesis)
            {
                return suggestedBlock;
            }

            // TODO: unimportant but out of curiosity, is the check faster than cast to nullable?
            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Applying DAO transition");
                }

                ApplyDaoTransition();
            }

            // TODO: should be precalculated
            Keccak transactionsRoot = GetTransactionsRoot(suggestedBlock.Transactions);
            if (transactionsRoot != suggestedBlock.Header.TransactionsRoot)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"TRANSACTIONS_ROOT {transactionsRoot} != TRANSACTIONS_ROOT {transactionsRoot}");
                }
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Block beneficiary {suggestedBlock.Header.Beneficiary}");
                _logger.Debug($"Block gas limit {suggestedBlock.Header.GasLimit}");
                _logger.Debug($"Block gas used {suggestedBlock.Header.GasUsed}");
                _logger.Debug($"Block difficulty {suggestedBlock.Header.Difficulty}");
            }

            Block processedBlock = ProcessBlock(
                suggestedBlock.Header.ParentHash,
                suggestedBlock.Header.Difficulty,
                suggestedBlock.Header.Number,
                suggestedBlock.Header.Timestamp,
                suggestedBlock.Header.Beneficiary,
                suggestedBlock.Header.GasLimit,
                suggestedBlock.Header.ExtraData,
                suggestedBlock.Transactions,
                suggestedBlock.Header.MixHash,
                suggestedBlock.Header.Nonce,
                suggestedBlock.Header.OmmersHash,
                suggestedBlock.Ommers);

            processedBlock.Transactions = suggestedBlock.Transactions;
            processedBlock.Header.TransactionsRoot = transactionsRoot;
            processedBlock.Header.Hash = BlockHeader.CalculateHash(processedBlock.Header);

            if (!tryOnly && !_blockValidator.ValidateProcessedBlock(processedBlock, suggestedBlock))
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Processed block is not valid {processedBlock.ToString(Block.Format.Short)}");
                    _logger.Debug("Throwing invalid block");
                }

                throw new InvalidBlockException($"{processedBlock}");
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Committing block - state root {_stateProvider.StateRoot}");
            }

            IDb db = _dbProvider.GetOrCreateStateDb(); // TODO: now totally synchronous but would need to pass the batch object / some sync item
            db.StartBatch();
            _stateProvider.CommitTree();
            _storageProvider.CommitTrees();
            db.CommitBatch();
            
            _dbProvider.Commit(_specProvider.GetSpec(suggestedBlock.Number));
            return processedBlock;
        }

        private Block ProcessBlock(
            Keccak parentHash,
            BigInteger difficulty,
            BigInteger number,
            BigInteger timestamp,
            Address beneficiary,
            long gasLimit,
            byte[] extraData,
            Transaction[] transactions,
            Keccak mixHash, ulong nonce,
            Keccak ommersHash,
            BlockHeader[] ommers)
        {
            BlockHeader header = new BlockHeader(parentHash, ommersHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
            header.MixHash = mixHash;
            header.Nonce = nonce;
            Block block = new Block(header, ommers);
            ProcessTransactions(block, transactions);
            ApplyMinerRewards(block);
            header.StateRoot = _stateProvider.StateRoot;
            return block;
        }

        private void ApplyMinerRewards(Block block)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("Applying miner rewards");
            }

            BlockReward[] rewards = _rewardCalculator.CalculateRewards(block);
            for (int i = 0; i < rewards.Length; i++)
            {
                if (!_stateProvider.AccountExists(rewards[i].Address))
                {
                    _stateProvider.CreateAccount(rewards[i].Address, rewards[i].Value);
                }
                else
                {
                    _stateProvider.UpdateBalance(rewards[i].Address, rewards[i].Value, _specProvider.GetSpec(block.Number));
                }
            }

            _stateProvider.Commit(_specProvider.GetSpec(block.Number));

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("Done applying miner rewards");
            }
        }
    }
}