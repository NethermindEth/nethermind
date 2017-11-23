using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Blockchain.Difficulty;
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
        private readonly TransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;

        private readonly IDifficultyCalculator _difficultyCalculator;

        private readonly IRewardCalculator _rewardCalculator;

        public BlockProcessor(IProtocolSpecification protocolSpecification, TransactionProcessor transactionProcessor, IStateProvider stateProvider, ILogger logger = null)
        {
            _protocolSpecification = protocolSpecification;
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
            _difficultyCalculator = new ProtocolBasedDifficultyCalculator(_protocolSpecification);
            _rewardCalculator = new RewardCalculator(_protocolSpecification);
        }

        private readonly IProtocolSpecification _protocolSpecification;

        private void ProcessTransactions(Block block, List<Transaction> transactions)
        {
            List<TransactionReceipt> receipts = new List<TransactionReceipt>(); // TODO: pool?
            for (int i = 0; i < transactions.Count; i++)
            {
                TransactionReceipt receipt = _transactionProcessor.Execute(transactions[i], block.Header);
                receipts.Add(receipt);
                transactions.Add(transactions[i]);
            }

            SetReceipts(block, receipts);
            SetTransactions(block, transactions);
        }

        private void SetReceipts(Block block, List<TransactionReceipt> receipts)
        {
            PatriciaTree receiptTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < receipts.Count; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _protocolSpecification.IsEip658Enabled);
                receiptTree.Set(Rlp.Encode(0).Bytes, receiptRlp);
            }

            block.Receipts = receipts;
            block.Header.ReceiptsRoot = receiptTree.RootHash;
            block.Header.Bloom = receipts.LastOrDefault()?.Bloom ?? block.Parent.Header.Bloom;
        }

        private void SetTransactions(Block block, List<Transaction> transactions)
        {
            PatriciaTree tranTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < transactions.Count; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            block.Transactions = transactions;
            block.Header.TransactionsRoot = tranTree.RootHash;
        }

        public Block ProcessBlock(Block parent, BigInteger timestamp, Address beneficiary, long gasLimit, byte[] extraData, List<Transaction> transactions, params BlockHeader[] uncles)
        {
            Keccak ommersHash = Keccak.Compute(Rlp.Encode(uncles.Cast<object>().ToList())); // TODO: refactor RLP here
            BigInteger blockNumber = parent.Header.Number + 1;
            BigInteger dificulty = _difficultyCalculator.Calculate(parent.Header.Difficulty, parent.Header.Timestamp, timestamp, blockNumber, uncles.Length > 0);

            BlockHeader header = new BlockHeader(parent.Hash, ommersHash, beneficiary, dificulty, blockNumber, gasLimit, timestamp, extraData);
            Block block = new Block(header, parent, uncles);
            ProcessTransactions(block, transactions);
            ApplyMinerRewards(block);

            header.Close();
            return block;
        }

        private void ApplyMinerRewards(Block block)
        {
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

                _stateProvider.Commit();
            }
        }
    }
}