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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.HashLib;
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
            List<TransactionReceipt> receipts = new List<TransactionReceipt>(); // TODO: pool?
            for (int i = 0; i < transactions.Length; i++)
            {
                var transaction = transactions[i];
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"PROCESSING TRANSACTION {i}");
                }

                _transactionStore.AddTransaction(transaction);
                TransactionReceipt receipt = _transactionProcessor.Execute(transaction, block.Header);
                Debug.Assert(transaction.Hash != null, "expecting only signed transactions here");
                _transactionStore.AddTransactionReceipt(transaction.Hash, receipt, block.Hash);
                receipts.Add(receipt);
            }

            SetReceipts(block, receipts);
        }

        private void SetReceipts(Block block, List<TransactionReceipt> receipts)
        {
            PatriciaTree receiptTree = receipts.Count > 0 ? new PatriciaTree() : null;
            for (int i = 0; i < receipts.Count; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _specProvider.GetSpec(block.Header.Number).IsEip658Enabled);
                receiptTree?.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            block.Receipts = receipts.ToArray();
            block.Header.ReceiptsRoot = receiptTree?.RootHash ?? PatriciaTree.EmptyTreeHash;
            block.Header.Bloom = receipts.Count > 0 ? receipts.Last().Bloom : Bloom.Empty;
        }

        private Keccak GetTransactionsRoot(Transaction[] transactions)
        {
            PatriciaTree tranTree = new PatriciaTree();
            for (int i = 0; i < transactions.Length; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            return tranTree.RootHash;
        }

        public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, bool tryOnly)
        {
            int dbSnapshot = _dbProvider.TakeSnapshot();
            Keccak snapshotStateRoot = _stateProvider.StateRoot;

            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                // discarding one of the branches
                _storageProvider.ClearCaches();
                _stateProvider.ClearCaches();
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
                    _stateProvider.ClearCaches();
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
                _stateProvider.ClearCaches();
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
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"HASH {suggestedBlock.Header.Hash} NUMBER {suggestedBlock.Header.Number}");
            }

            if (suggestedBlock.IsGenesis)
            {
                return suggestedBlock;
            }

            // TODO: unimportant but out of curiosity, is the check faster than cast to nullable?
            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"APPLYING DAO TRANSITION");
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
                _logger.Debug($"BLOCK BENEFICIARY {suggestedBlock.Header.Beneficiary}");
                _logger.Debug($"BLOCK GAS LIMIT {suggestedBlock.Header.GasLimit}");
                _logger.Debug($"BLOCK GAS USED {suggestedBlock.Header.GasUsed}");
                _logger.Debug($"BLOCK DIFFICULTY {suggestedBlock.Header.Difficulty}");
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
                    _logger.Info($"PROCESSED BLOCK IS NOT VALID {processedBlock.Hash} ({processedBlock.Number})");
                    _logger.Debug($"THROWING INVALID BLOCK");
                }

                throw new InvalidBlockException();
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"COMMITING BLOCK - STATE ROOT {_stateProvider.StateRoot}");
            }

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
                _logger.Debug("APPLYING MINER REWARDS");
            }

            Dictionary<Address, BigInteger> rewards = _rewardCalculator.CalculateRewards(block);
            foreach ((Address address, BigInteger reward) in rewards)
            {
                if (!_stateProvider.AccountExists(address))
                {
                    _stateProvider.CreateAccount(address, reward);
                }
                else
                {
                    _stateProvider.UpdateBalance(address, reward, _specProvider.GetSpec(block.Number));
                }
            }

            _stateProvider.Commit(_specProvider.GetSpec(block.Number));

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("DONE APPLYING MINER REWARDS");
            }
        }
    }
}