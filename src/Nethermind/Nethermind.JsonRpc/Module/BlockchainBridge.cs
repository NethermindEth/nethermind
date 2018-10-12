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
using System.Security;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.KeyStore;
using Nethermind.Store;

namespace Nethermind.JsonRpc.Module
{
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly ITxTracer _txTracer;
        private readonly IBlockTree _blockTree;
        private readonly IKeyStore _keyStore;
        private readonly IEthereumSigner _signer;
        private readonly IDb _stateDb;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionStore _transactionStore;
        private readonly IFilterStore _filterStore;
        private Dictionary<string, IDb> _dbMappings;

        public BlockchainBridge(IEthereumSigner signer,
            IStateProvider stateProvider,
            IKeyStore keyStore,
            IBlockTree blockTree,
            IBlockchainProcessor blockchainProcessor,
            ITxTracer txTracer,
            IDbProvider dbProvider,
            ITransactionStore transactionStore,
            IFilterStore filterStore)
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _txTracer = txTracer ?? throw new ArgumentNullException(nameof(txTracer));
            _stateDb = dbProvider?.StateDb ?? throw new ArgumentNullException(nameof(dbProvider.StateDb));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            
            IDb blockInfosDb = dbProvider?.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider?.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb txDb = dbProvider?.TxDb ?? throw new ArgumentNullException(nameof(dbProvider.TxDb));
            IDb receiptsDb = dbProvider?.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider?.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));

            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, _stateDb},
                {DbNames.Storage, _stateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Code, codeDb},
                {DbNames.Transactions, txDb},
                {DbNames.Receipts, receiptsDb}
            };
        }

        public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses()
        {
            return _keyStore.GetKeyAddresses();
        }

        public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            return _keyStore.GetKey(address, password);
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

        public Transaction GetTransaction(Keccak transactionHash)
        {
            TxInfo txInfo = _transactionStore.GetTxInfo(transactionHash);
            Block block = _blockTree.FindBlock(txInfo.BlockHash, true);
            return block.Transactions[txInfo.Index];
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _transactionStore.GetTxInfo(transactionHash).BlockHash;
        }

        public void SendTransaction(Transaction transaction)
        {
            PrivateKey mock = new PrivateKeyProvider(new CryptoRandom()).PrivateKey;
            transaction.SenderAddress = mock.Address;
            _signer.Sign(mock, transaction, _blockTree.Head.Number);
            transaction.Hash = Transaction.CalculateHash(transaction);

            if (_signer.RecoverAddress(transaction, _blockTree.Head.Number) != transaction.SenderAddress)
            {
                throw new InvalidOperationException("Invalid signature");
            }

            if (_stateProvider.GetNonce(transaction.SenderAddress) != transaction.Nonce)
            {
                throw new InvalidOperationException("Invalid nonce");
            }

            _transactionStore.AddPending(transaction);
        }

        public TransactionReceipt GetTransactionReceipt(Keccak transactionHash)
        {
            return _transactionStore.GetReceipt(transactionHash);
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
            return _filterStore.CreateFilter(fromBlock.Hash, toBlock.Hash, address, topics).FilterId;
        }

        public int NewBlockFilter()
        {
            return _filterStore.CreateBlockFilter(_blockTree.Head.Number).FilterId;
        }

        public object[] GetFilterChanges(int filterId)
        {
            return new object[] {_blockTree.Head.Hash};
        }
    }
}