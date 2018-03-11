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
using Nethermind.Core.Potocol;
using Nethermind.Evm;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ISnapshotable _db;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ILogger _logger;
        private readonly ITransactionStore _transactionStore;

        private readonly IDifficultyCalculator _difficultyCalculator;

        private readonly IRewardCalculator _rewardCalculator;

        public BlockProcessor(
            IEthereumRelease ethereumRelease,
            IBlockStore blockStore,
            IBlockValidator blockValidator,
            IDifficultyCalculator difficultyCalculator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            IMultiDb db,
            IStateProvider stateProvider,
            IStorageProvider storageProvider, ITransactionStore transactionStore, ILogger logger = null)
        {
            _logger = logger;
            _ethereumRelease = ethereumRelease;
            _blockStore = blockStore;
            _blockValidator = blockValidator;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _transactionStore = transactionStore;
            _difficultyCalculator = difficultyCalculator;
            _rewardCalculator = rewardCalculator;
            _transactionProcessor = transactionProcessor;
            _db = db;
        }

        private readonly IEthereumRelease _ethereumRelease;
        private readonly IBlockStore _blockStore;
        private readonly IBlockValidator _blockValidator;

        private void ProcessTransactions(Block block, List<Transaction> transactions)
        {
            List<TransactionReceipt> receipts = new List<TransactionReceipt>(); // TODO: pool?
            for (int i = 0; i < transactions.Count; i++)
            {
                var transaction = transactions[i];
                if (block.Header.Number == 26)
                {
                    
                }
                
                _logger?.Log($"PROCESSING TRANSACTION {i}");
                _transactionStore.AddTransaction(transaction);
                TransactionReceipt receipt = _transactionProcessor.Execute(transaction, block.Header);
                Debug.Assert(transaction.Hash.HasValue, "expecting only signed transactions here");
                _transactionStore.AddTransactionReceipt(transaction.Hash.Value, receipt, block.Hash);
                receipts.Add(receipt);
            }

            SetReceipts(block, receipts);
        }

        private void SetReceipts(Block block, List<TransactionReceipt> receipts)
        {
            PatriciaTree receiptTree = receipts.Count > 0 ? new PatriciaTree() : null;
            for (int i = 0; i < receipts.Count; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _ethereumRelease.IsEip658Enabled);   
                receiptTree.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            block.Receipts = receipts;
            block.Header.ReceiptsRoot = receiptTree?.RootHash ?? PatriciaTree.EmptyTreeHash;
            block.Header.Bloom = receipts.Count > 0 ? receipts.Last().Bloom : Bloom.EmptyBloom;
        }

        private Keccak GetTransactionsRoot(List<Transaction> transactions)
        {
            PatriciaTree tranTree = new PatriciaTree();
            for (int i = 0; i < transactions.Count; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            return tranTree.RootHash;
        }

        public Block[] Process(Keccak? branchStateRoot, Block[] suggestedBlocks)
        {
            int dbSnapshot = _db.TakeSnapshot();
            Keccak snapshotStateRoot = _stateProvider.StateRoot;
            
            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                // discarding one of the branches
                _storageProvider.ClearCaches();
                _stateProvider.ClearCaches();
                _stateProvider.StateRoot = branchStateRoot.Value;
            }

            Block[] processedBlocks = new Block[suggestedBlocks.Length];
            try
            {
                for (int i = 0; i < suggestedBlocks.Length; i++)
                {
                    processedBlocks[i] = ValidateAndProcessBlock(suggestedBlocks[i]);   
                }

                return processedBlocks;
            }
            catch (InvalidBlockException) // TODO: which exception to catch here?
            {
                _logger?.Log($"REVERTING BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                _db.Restore(dbSnapshot);
                _storageProvider.ClearCaches();
                _stateProvider.ClearCaches();
                _stateProvider.StateRoot = snapshotStateRoot;
                _logger?.Log($"REVERTED BLOCKS - STATE ROOT {_stateProvider.StateRoot}");
                throw;
            }
        }

        private Block ValidateAndProcessBlock(Block suggestedBlock) // TODO: refactor
        {
            _logger?.Log($"HASH {suggestedBlock.Header.Hash} NUMBER {suggestedBlock.Header.Number}");
            if (!_blockValidator.ValidateSuggestedBlock(suggestedBlock))
            {
                throw new InvalidBlockException();
            }

            if (suggestedBlock.IsGenesis)
            {
                return suggestedBlock; // TODO: genesis validation should probably be more strict
            }

            Block parent = _blockStore.FindParent(suggestedBlock.Header);
            if (parent == null)
            {
                _logger?.Log($"DISCARDING BLOCK - COULD NOT FIND PARENT OF {suggestedBlock.Header.Hash} (child of {suggestedBlock.Header.ParentHash}) {suggestedBlock.Header.Number}");
                throw new InvalidBlockException();
            }

            Keccak transactionsRoot = GetTransactionsRoot(suggestedBlock.Transactions);
            BigInteger blockNumber = parent.Header.Number + 1;
            BigInteger difficulty = _difficultyCalculator.Calculate(parent.Header.Difficulty, parent.Header.Timestamp, suggestedBlock.Header.Timestamp, blockNumber, parent.Ommers.Length > 0);
            Keccak ommersHash = Keccak.Compute(Rlp.Encode(suggestedBlock.Ommers)); // TODO: refactor RLP here
            if (transactionsRoot != suggestedBlock.Header.TransactionsRoot ||
                blockNumber != suggestedBlock.Header.Number ||
                difficulty != suggestedBlock.Header.Difficulty ||
                ommersHash != suggestedBlock.Header.OmmersHash)
            {
                throw new InvalidBlockException();
            }

            _logger?.Log($"BLOCK BENEFICIARY {suggestedBlock.Header.Beneficiary}");
            _logger?.Log($"BLOCK GAS LIMIT {suggestedBlock.Header.GasLimit}");
            _logger?.Log($"BLOCK GAS USED {suggestedBlock.Header.GasUsed}");
            _logger?.Log($"BLOCK DIFFICULTY {suggestedBlock.Header.Difficulty}");

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

            // TODO: will need to validate again the list of transactions after processing instead
            processedBlock.Transactions = suggestedBlock.Transactions;
            processedBlock.Header.TransactionsRoot = suggestedBlock.Header.TransactionsRoot;
            processedBlock.Header.RecomputeHash();

            if (!_blockValidator.ValidateProcessedBlock(processedBlock, suggestedBlock))
            {
                throw new InvalidBlockException();
            }
            
            _logger?.Log($"COMMITING BLOCK - STATE ROOT {_stateProvider.StateRoot}");
            _db.Commit();
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
            List<Transaction> transactions,
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
            _logger?.Log("APPLYING MINER REWARDS");
            Dictionary<Address, BigInteger> rewards = _rewardCalculator.CalculateRewards(block);
            foreach ((Address address, BigInteger reward) in rewards)
            {
                if (!_stateProvider.AccountExists(address))
                {
                    _stateProvider.CreateAccount(address, reward);
                }
                else
                {
                    _stateProvider.UpdateBalance(address, reward);
                }
            }
            
            _stateProvider.Commit();
            _logger?.Log("DONE APPLYING MINER REWARDS");
        }
    }
}