using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Blockchain.Difficulty;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;
using Nevermind.Evm;
using Nevermind.Store;

namespace Nevermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ISnapshotable _db;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ILogger _logger;

        private readonly IDifficultyCalculator _difficultyCalculator;

        private readonly IRewardCalculator _rewardCalculator;

        public BlockProcessor(
            IProtocolSpecification protocolSpecification,
            IBlockchainStore blockchainStore,
            IBlockValidator blockValidator,
            IDifficultyCalculator difficultyCalculator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotable db,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ILogger logger = null)
        {
            _logger = logger;
            _protocolSpecification = protocolSpecification;
            _blockchainStore = blockchainStore;
            _blockValidator = blockValidator;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _difficultyCalculator = difficultyCalculator;
            _rewardCalculator = rewardCalculator;
            _transactionProcessor = transactionProcessor;
            _db = db;
        }

        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IBlockchainStore _blockchainStore;
        private readonly IBlockValidator _blockValidator;

        private void ProcessTransactions(Block block, List<Transaction> transactions)
        {
            List<TransactionReceipt> receipts = new List<TransactionReceipt>(); // TODO: pool?
            for (int i = 0; i < transactions.Count; i++)
            {
                _logger?.Log($"PROCESSING TRANSACTION {i}");
                TransactionReceipt receipt = _transactionProcessor.Execute(transactions[i], block.Header);
                receipts.Add(receipt);
            }

            SetReceipts(block, receipts);
        }

        private void SetReceipts(Block block, List<TransactionReceipt> receipts)
        {
            PatriciaTree receiptTree = new PatriciaTree();
            for (int i = 0; i < receipts.Count; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _protocolSpecification.IsEip658Enabled);
                receiptTree.Set(Rlp.Encode(0).Bytes, receiptRlp);
            }

            block.Receipts = receipts;
            block.Header.ReceiptsRoot = receiptTree.RootHash;
            block.Header.Bloom = receipts.LastOrDefault()?.Bloom ?? block.Header.Bloom;
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

        public Block Process(Rlp rlp)
        {
            _logger?.Log("PROCESSING BLOCK");
            int dbSnapshot = _db.TakeSnapshot();
            Keccak stateRoot = _stateProvider.StateRoot;
            try
            {
                Block suggestedBlock = Rlp.Decode<Block>(rlp);
                _logger?.Log($"HASH {suggestedBlock.Header.Hash} NUMBER {suggestedBlock.Header.Number}");
                if (!_blockValidator.ValidateSuggestedBlock(suggestedBlock))
                {
                    throw new InvalidBlockException(rlp);
                }

                if (suggestedBlock.IsGenesis)
                {
                    return suggestedBlock; // TODO: genesis validation should probably be more strict
                }

                Block parent = _blockchainStore.FindBlock(suggestedBlock.Header.ParentHash);
                if (parent == null)
                {
                    _logger?.Log($"DISCARDING BLOCK - COULD NOT FIND PARENT OF {suggestedBlock.Header.Hash} (child of {suggestedBlock.Header.ParentHash}) {suggestedBlock.Header.Number}");
                    throw new InvalidBlockException(rlp);
                }
                
                Keccak transactionsRoot = GetTransactionsRoot(suggestedBlock.Transactions);
                BigInteger blockNumber = parent.Header.Number + 1;
                BigInteger difficulty =  _difficultyCalculator.Calculate(parent.Header.Difficulty, parent.Header.Timestamp, suggestedBlock.Header.Timestamp, blockNumber, parent.Ommers.Length > 0);
                Keccak ommersHash = Keccak.Compute(Rlp.Encode(suggestedBlock.Ommers)); // TODO: refactor RLP here
                if (transactionsRoot != suggestedBlock.Header.TransactionsRoot ||
                    blockNumber != suggestedBlock.Header.Number ||
                    difficulty != suggestedBlock.Header.Difficulty ||
                    ommersHash != suggestedBlock.Header.OmmersHash)
                {
                    throw new InvalidBlockException(rlp);
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
                
                // TODO: will need to validate again the list of transactions after processing instead
                processedBlock.Transactions = suggestedBlock.Transactions;
                processedBlock.Header.TransactionsRoot = suggestedBlock.Header.TransactionsRoot;
                processedBlock.Header.RecomputeHash();
                
                if (!_blockValidator.ValidateProcessedBlock(processedBlock, suggestedBlock))
                {
                    throw new InvalidBlockException(rlp);
                }

                // TODO: at the moment I cannot calculate state root without committing...
                // TODO: can I revert by assigning previous state root?
                // TODO: how to clean DB in such case?
                // TODO: can I calculate state root on the fly...?
                // TODO: need to add CommitDB, RestoreDB, TakeDBSnapshot... sooo... two level snapshots, one restoring calls, one restoring blocks
                // TODO: DB changes can be easily stored for each block, if we want to revert them

                _logger?.Log("COMMITING BLOCK");
                _db.Commit();
                return processedBlock;
            }
            catch (InvalidBlockException) // TODO: which exception to catch here?
            {
                _logger?.Log("REVERTING BLOCK");
                _db.Restore(dbSnapshot);
                _storageProvider.ClearCaches();
                _stateProvider.ClearCaches();
                _stateProvider.StateRoot = stateRoot;
                throw;
            }
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