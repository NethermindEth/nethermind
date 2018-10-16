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
    [Todo("Split the class into separate modules / bridges")]
    [Todo("Any state requests should be taken from specified state snapshot (potentially current)")]
    [Todo("We need a concurrent State representation that can track idnependently from a given state root")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockTree _blockTree;
        private readonly IFilterStore _filterStore;
        private readonly IEthereumSigner _signer;
        private readonly IDb _stateDb;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionStore _transactionStore;
        private readonly ITxTracer _txTracer;
        private readonly IWallet _wallet;
        private Dictionary<string, IDb> _dbMappings;

        public BlockchainBridge(IEthereumSigner signer,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            IBlockchainProcessor blockchainProcessor,
            ITxTracer txTracer,
            IDbProvider dbProvider,
            ITransactionStore transactionStore,
            IFilterStore filterStore,
            IWallet wallet)
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _txTracer = txTracer ?? throw new ArgumentNullException(nameof(txTracer));
            _stateDb = dbProvider?.StateDb ?? throw new ArgumentNullException(nameof(dbProvider.StateDb));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _wallet = wallet ?? throw new ArgumentException(nameof(wallet));

            IDb blockInfosDb = dbProvider?.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider?.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb receiptsDb = dbProvider?.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider?.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));

            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, _stateDb},
                {DbNames.Storage, _stateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Code, codeDb},
                {DbNames.Receipts, receiptsDb}
            };
        }

        public IReadOnlyCollection<Address>  GetWalletAccounts()
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

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            return _signer.Sign(privateKey, message);
        }

        public void AddTxData(UInt256 blockNumber)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            if (block == null) throw new InvalidOperationException("Only blocks from the past");

            _blockchainProcessor.AddTxData(block);
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

            if (_signer.RecoverAddress(transaction, _blockTree.Head.Number) != transaction.SenderAddress) throw new InvalidOperationException("Invalid signature");

            if (_stateProvider.GetNonce(transaction.SenderAddress) != transaction.Nonce) throw new InvalidOperationException("Invalid nonce");

            _transactionStore.AddPending(transaction);
            return transaction.Hash;
        }

        public TransactionReceipt GetTransactionReceipt(Keccak txHash)
        {
            return _transactionStore.GetReceipt(txHash);
        }

        public TransactionTrace GetTransactionTrace(Keccak transactionHash)
        {
            return _txTracer.Trace(transactionHash);
        }

        public TransactionTrace GetTransactionTrace(UInt256 blockNumber, int index)
        {
            return _txTracer.Trace(blockNumber, index);
        }

        public TransactionTrace GetTransactionTrace(Keccak blockHash, int index)
        {
            return _txTracer.Trace(blockHash, index);
        }

        public BlockTrace GetBlockTrace(Keccak blockHash)
        {
            return _txTracer.TraceBlock(blockHash);
        }

        public BlockTrace GetBlockTrace(UInt256 blockNumber)
        {
            return _txTracer.TraceBlock(blockNumber);
        }

        public byte[] Call(Block block, Transaction transaction)
        {
            _stateProvider.StateRoot = block.StateRoot;
            transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
            transaction.Hash = Transaction.CalculateHash(transaction);
            return Bytes.FromHexString(_txTracer.Trace(block.Number, transaction).ReturnValue);
        }

        public byte[] GetDbValue(string dbName, byte[] key)
        {
            return _dbMappings[dbName][key];
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
            StateTree stateTree = new StateTree(_stateDb, stateRoot);
            return stateTree.Get(address);
        }

        public int GetNetworkId()
        {
            return _blockTree.ChainId;
        }

        public int NewFilter(FilterBlock fromBlock, FilterBlock toBlock,
            object address = null, IEnumerable<object> topics = null)
        {
            return _filterStore.CreateFilter(fromBlock, toBlock, address, topics).Id;
        }

        public int NewBlockFilter()
        {
            return _filterStore.CreateBlockFilter(_blockTree.Head.Number).Id;
        }

        public void UninstallFilter(int filterId)
        {
            _filterStore.RemoveFilter(filterId);
        }

        public object[] GetFilterChanges(int filterId)
        {
            return new object[] {_blockTree.Head.Hash};
        }
    }
}