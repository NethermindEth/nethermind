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
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
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
            IStorageProvider storageProvider, ITransactionStore transactionStore, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));;
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));;
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));;
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));;
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));;
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));;
            _dbProvider = dbProvider;
        }

        private readonly IBlockValidator _blockValidator;

        private void ProcessTransactions(Block block, Transaction[] transactions)
        {
            TransactionReceipt[] receipts = new TransactionReceipt[transactions.Length];
            for (int i = 0; i < transactions.Length; i++)
            {
                var transaction = transactions[i];
                if (_logger.IsTrace) _logger.Trace($"Processing transaction {i}");

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
            PatriciaTree receiptTree = receipts.Length > 0 ? new PatriciaTree(NullDb.Instance, Keccak.EmptyTreeHash, false) : null;
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _specProvider.GetSpec(block.Header.Number).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                receiptTree?.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            receiptTree?.UpdateRootHash();

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
            if (suggestedBlocks.Length == 0)
            {
                return Array.Empty<Block>();
            }
            
            IDb db = _dbProvider.GetOrCreateStateDb();
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
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"REVERTING BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                    }

                    _dbProvider.Restore(dbSnapshot);
                    _storageProvider.ClearCaches();
                    _stateProvider.Reset();
                    _stateProvider.StateRoot = snapshotStateRoot;

                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"REVERTED BLOCKS (JUST VALIDATED FOR MINING) - STATE ROOT {_stateProvider.StateRoot}");
                    }
                }
                else
                {
                    db.StartBatch();
                    _dbProvider.Commit(_specProvider.GetSpec(suggestedBlocks[0].Number));
                    db.CommitBatch();
                }

                return processedBlocks;
            }
            catch (InvalidBlockException) // TODO: which exception to catch here?
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"REVERTING BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                }

                _dbProvider.Restore(dbSnapshot);
                _storageProvider.ClearCaches();
                _stateProvider.Reset();
                _stateProvider.StateRoot = snapshotStateRoot;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"REVERTED BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                }

                if (_logger.IsWarn)
                {
                    _logger.Warn($"Invalid block");
                }

                throw;
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

        private Block ProcessOne(Block suggestedBlock, bool tryOnly)
        {
            Block processedBlock = suggestedBlock;
            if (!suggestedBlock.IsGenesis)
            {
                processedBlock = ProcessNonGenesis(suggestedBlock, tryOnly);
            }

            _stateProvider.CommitTree();
            _storageProvider.CommitTrees();
            
            return processedBlock;
        }

        private Block ProcessNonGenesis(Block suggestedBlock, bool tryOnly)
        {
            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Applying DAO transition");
                }

                ApplyDaoTransition();
            }

            // TODO: should be precalculated
            Keccak transactionsRoot = GetTransactionsRoot(suggestedBlock.Transactions);
            if (transactionsRoot != suggestedBlock.Header.TransactionsRoot)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"TRANSACTIONS_ROOT {transactionsRoot} != TRANSACTIONS_ROOT {transactionsRoot}");
                }
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Block beneficiary {suggestedBlock.Header.Beneficiary}");
                _logger.Trace($"Block gas limit {suggestedBlock.Header.GasLimit}");
                _logger.Trace($"Block gas used {suggestedBlock.Header.GasUsed}");
                _logger.Trace($"Block difficulty {suggestedBlock.Header.Difficulty}");
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
                if (_logger.IsError)
                {
                    _logger.Error($"Processed block is not valid {processedBlock.ToString(Block.Format.Short)}");
                }

                throw new InvalidBlockException($"{processedBlock}");
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Committing block - state root {_stateProvider.StateRoot}");
            }

            return processedBlock;
        }

        private Block ProcessBlock(
            Keccak parentHash,
            UInt256 difficulty,
            UInt256 number,
            UInt256 timestamp,
            Address beneficiary,
            long gasLimit,
            byte[] extraData,
            Transaction[] transactions,
            Keccak mixHash,
            ulong nonce,
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
            if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
            BlockReward[] rewards = _rewardCalculator.CalculateRewards(block);
            for (int i = 0; i < rewards.Length; i++)
            {
                if(_logger.IsTrace) _logger.Trace($"    {((decimal)rewards[i].Value / (decimal)Unit.Ether):N3}{Unit.EthSymbol} for account at {rewards[i].Address}");
                if (!_stateProvider.AccountExists(rewards[i].Address))
                {
                    _stateProvider.CreateAccount(rewards[i].Address, (UInt256)rewards[i].Value);
                }
                else
                {   
                    _stateProvider.AddToBalance(rewards[i].Address, (UInt256)rewards[i].Value, _specProvider.GetSpec(block.Number));
                }
            }

            _stateProvider.Commit(_specProvider.GetSpec(block.Number));
        }
    }
}