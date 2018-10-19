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
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Module
{
    [DoNotUseInSecuredContext("Not reviewed, work in progress")]
    public class BlockchainBridge : IBlockchainBridge
    {
        
        private readonly IBlockTree _blockTree;
        private readonly IFilterManager _filterManager;
        private readonly IFilterStore _filterStore;
        private readonly IEthereumSigner _signer;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ITransactionStore _transactionStore;
        private readonly IWallet _wallet;


        public BlockchainBridge(IEthereumSigner signer,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            ITransactionStore transactionStore,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IWallet wallet,
            ITransactionProcessor transactionProcessor)
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentException(nameof(filterManager));
            _wallet = wallet ?? throw new ArgumentException(nameof(wallet));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentException(nameof(transactionProcessor));
        }

        public IReadOnlyCollection<Address> GetWalletAccounts()
        {
            return _wallet.GetAccounts();
        }

        public Signature Sign(Address address, Keccak message)
        {
            return _wallet.Sign(address, message);
        }

        public BlockHeader Head => _blockTree.Head;
        public BlockHeader BestSuggested => _blockTree.BestSuggested;

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            return _blockTree.FindBlock(blockHash, mainChainOnly);
        }

        public Block FindBlock(UInt256 blockNumber)
        {
            return _blockTree.FindBlock(blockNumber);
        }

        public Block RetrieveHeadBlock()
        {
            return _blockTree.FindBlock(_blockTree.Head.Hash, false);
        }

        public Block RetrieveGenesisBlock()
        {
            return _blockTree.FindBlock(_blockTree.Genesis.Hash, true);
        }

        public (TransactionReceipt Receipt, Transaction Transaction) GetTransaction(Keccak transactionHash)
        {
            TransactionReceipt receipt = _transactionStore.GetReceipt(transactionHash);
            if (receipt.BlockHash == null) return (null, null);

            Block block = _blockTree.FindBlock(receipt.BlockHash, true);
            return (receipt, block.Transactions[receipt.Index]);
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _transactionStore.GetReceipt(transactionHash).BlockHash;
        }

        public Keccak SendTransaction(Transaction transaction)
        {
            _stateProvider.StateRoot = _blockTree.Head.StateRoot;

            if (transaction.SenderAddress == null) transaction.SenderAddress = _wallet.GetAccounts()[0];

            transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
            _wallet.Sign(transaction, _blockTree.ChainId);
            transaction.Hash = Transaction.CalculateHash(transaction);

            if (_stateProvider.GetNonce(transaction.SenderAddress) != transaction.Nonce)
            {
                throw new InvalidOperationException("Invalid nonce");
            }

            _transactionStore.AddPending(transaction, _blockTree.Head.Number);
            return transaction.Hash;
        }

        public TransactionReceipt GetTransactionReceipt(Keccak txHash)
        {
            return _transactionStore.GetReceipt(txHash);
        }

        public byte[] Call(Block block, Transaction transaction)
        {
            BlockHeader header = new BlockHeader(block.Hash, Keccak.OfAnEmptySequenceRlp, block.Beneficiary, block.Difficulty, block.Number + 1, (long) transaction.GasLimit, block.Timestamp + 1, Bytes.Empty);
            transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
            transaction.Hash = Transaction.CalculateHash(transaction);
            (TransactionReceipt receipt, TransactionTrace trace) = _transactionProcessor.CallAndRestore(0, transaction, header, true);
            return Bytes.FromHexString(trace.ReturnValue);
        }

        public byte[] GetCode(Address address)
        {
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(Keccak codeHash)
        {
            return _stateProvider.GetCode(codeHash);
        }

        public BigInteger GetNonce(Address address)
        {
            return _stateProvider.GetNonce(address);
        }

        public BigInteger GetBalance(Address address)
        {
            return _stateProvider.GetBalance(address);
        }

        public Account GetAccount(Address address, Keccak stateRoot)
        {
            _stateProvider.StateRoot = stateRoot;
            return _stateProvider.GetAccount(address);
        }

        public int GetNetworkId()
        {
            return _blockTree.ChainId;
        }

        public bool FilterExists(int filterId)
        {
            return _filterStore.FilterExists(filterId);
        }

        public FilterType GetFilterType(int filterId)
        {
            return _filterStore.GetFilterType(filterId);
        }

        public FilterLog[] GetFilterLogs(int filterId)
        {
            return _filterManager.GetLogs(filterId);
        }

        public FilterLog[] GetLogs(FilterBlock fromBlock, FilterBlock toBlock, object address = null,
            IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return new FilterLog[0];
        }

        public int NewFilter(FilterBlock fromBlock, FilterBlock toBlock,
            object address = null, IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = _filterStore.CreateBlockFilter(_blockTree.Head.Number);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public void UninstallFilter(int filterId)
        {
            _filterStore.RemoveFilter(filterId);
        }

        public FilterLog[] GetLogFilterChanges(int filterId)
        {
            return _filterManager.PollLogs(filterId);
        }

        public Keccak[] GetBlockFilterChanges(int filterId)
        {
            return _filterManager.PollBlockHashes(filterId);
        }

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            return _signer.Sign(privateKey, message);
        }
    }
}