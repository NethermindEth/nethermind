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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Store;
using Nethermind.Wallet;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    [DoNotUseInSecuredContext("Not reviewed, work in progress")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly ITxPool _txPool;
        private readonly IWallet _wallet;
        private readonly IBlockTree _blockTree;
        private readonly IFilterStore _filterStore;
        private readonly IStateReader _stateReader;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IFilterManager _filterManager;
        private readonly IStateProvider _stateProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IStorageProvider _storageProvider;
        private readonly ITransactionProcessor _transactionProcessor;

        public BlockchainBridge(
            IStateReader stateReader,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            IBlockTree blockTree,
            ITxPool txPool,
            IReceiptStorage receiptStorage,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IWallet wallet,
            ITransactionProcessor transactionProcessor,
            IEthereumEcdsa ecdsa)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentException(nameof(filterManager));
            _wallet = wallet ?? throw new ArgumentException(nameof(wallet));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentException(nameof(transactionProcessor));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        }

        public IReadOnlyCollection<Address> GetWalletAccounts()
        {
            return _wallet.GetAccounts();
        }

        public Signature Sign(Address address, Keccak message)
        {
            return _wallet.Sign(message, address);
        }

        public void Sign(Transaction tx)
        {
            _wallet.Sign(tx, _blockTree.ChainId);
        }

        public BlockHeader Head => _blockTree.Head;
        
        public BlockHeader BestSuggested => _blockTree.BestSuggestedHeader;
        
        public long BestKnown => _blockTree.BestKnownNumber;
        
        public bool IsSyncing => _blockTree.BestSuggestedHeader.Hash != _blockTree.Head.Hash;
        
        public Block FindBlock(Keccak blockHash) => _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
        
        public Block FindBlock(long blockNumber) => _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        
        public Block FindGenesisBlock() => _blockTree.FindBlock(_blockTree.Genesis.Hash, BlockTreeLookupOptions.RequireCanonical);
        
        public Block FindHeadBlock() => _blockTree.FindBlock(_blockTree.Head.Hash, BlockTreeLookupOptions.None);
        
        public Block FindEarliestBlock() => FindGenesisBlock();
        
        public Block FindLatestBlock() => FindHeadBlock();

        public Block FindPendingBlock()
        {
            return _blockTree.FindBlock(_blockTree.BestSuggestedHeader?.Hash, BlockTreeLookupOptions.None);
        }

        public (TxReceipt Receipt, Transaction Transaction) GetTransaction(Keccak transactionHash)
        {
            TxReceipt txReceipt = _receiptStorage.Find(transactionHash);
            if (txReceipt?.BlockHash == null) return (null, null);

            Block block = _blockTree.FindBlock(txReceipt.BlockHash, BlockTreeLookupOptions.RequireCanonical);
            return (txReceipt, block?.Transactions[txReceipt.Index]);
        }

        public Keccak GetBlockHash(Keccak transactionHash) => _receiptStorage.Find(transactionHash).BlockHash;

        private Timestamper _timestamper = new Timestamper();

        public Keccak SendTransaction(Transaction transaction, bool isOwn = false)
        {
            _stateProvider.StateRoot = _blockTree.Head.StateRoot;

            transaction.Hash = Transaction.CalculateHash(transaction);
            transaction.Timestamp = _timestamper.EpochSeconds;

            var result = _txPool.AddTransaction(transaction, _blockTree.Head.Number, isOwn);
            if (isOwn && result == AddTxResult.OwnNonceAlreadyUsed)
            {
                transaction.Nonce = _txPool.ReserveOwnTransactionNonce(transaction.SenderAddress);
                Sign(transaction);
                transaction.Hash = Transaction.CalculateHash(transaction);
                _txPool.AddTransaction(transaction, _blockTree.Head.Number, true);
            }

            _stateProvider.Reset();
            return transaction.Hash;
        }

        public TxReceipt GetReceipt(Keccak txHash)
        {
            var txReceipt = _receiptStorage.Find(txHash);
            txReceipt.TxHash = txHash;
            return txReceipt;
        }

        public class CallOutput
        {
            public CallOutput()
            {
            }

            public CallOutput(byte[] outputData, long gasSpent, string error)
            {
                Error = error;
                OutputData = outputData;
                GasSpent = gasSpent;
            }

            public string Error { get; set; }

            public byte[] OutputData { get; set; }

            public long GasSpent { get; set; }
        }

        public CallOutput Call(BlockHeader blockHeader, Transaction transaction)
        {
            if (transaction.SenderAddress == null)
            {
                transaction.SenderAddress = Address.Zero;
            }

            _stateProvider.StateRoot = _blockTree.Head.StateRoot;
            BlockHeader header = new BlockHeader(blockHeader.Hash, Keccak.OfAnEmptySequenceRlp, blockHeader.Beneficiary,
                blockHeader.Difficulty, blockHeader.Number + 1, (long) transaction.GasLimit, blockHeader.Timestamp + 1, Bytes.Empty);
            transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
            transaction.Hash = Transaction.CalculateHash(transaction);
            CallOutputTracer callOutputTracer = new CallOutputTracer();
            _transactionProcessor.CallAndRestore(transaction, header, callOutputTracer);
            _stateProvider.Reset();
            _storageProvider.Reset();
            return new CallOutput {Error = callOutputTracer.Error, GasSpent = callOutputTracer.GasSpent, OutputData = callOutputTracer.ReturnValue};
        }

        public long EstimateGas(Block block, Transaction transaction)
        {
            _stateProvider.StateRoot = _blockTree.Head.StateRoot;
            BlockHeader header = new BlockHeader(block.Hash, Keccak.OfAnEmptySequenceRlp, block.Beneficiary,
                block.Difficulty, block.Number + 1, block.GasLimit, block.Timestamp + 1, Bytes.Empty);
            transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
            transaction.Hash = Nethermind.Core.Transaction.CalculateHash(transaction);
            CallOutputTracer callOutputTracer = new CallOutputTracer();
            _transactionProcessor.CallAndRestore(transaction, header, callOutputTracer);
            _stateProvider.Reset();
            _storageProvider.Reset();
            return callOutputTracer.GasSpent;
        }

        public byte[] GetCode(Address address)
        {
            return _stateReader.GetCode(_blockTree.Head.StateRoot, address);
        }

        public byte[] GetCode(Keccak codeHash)
        {
            return _stateReader.GetCode(codeHash);
        }

        public UInt256 GetNonce(Address address)
        {
            return _stateReader.GetNonce(_blockTree.Head.StateRoot, address);
        }

        public UInt256 GetBalance(Address address)
        {
            return _stateReader.GetBalance(_blockTree.Head.StateRoot, address);
        }

        public byte[] GetStorage(Address address, UInt256 index)
        {
            return GetStorage(address, index, _blockTree.Head.StateRoot);
        }

        public byte[] GetStorage(Address address, UInt256 index, Keccak stateRoot)
        {
            _stateProvider.StateRoot = stateRoot;
            return _storageProvider.Get(new StorageAddress(address, index));
        }

        public Account GetAccount(Address address)
        {
            return GetAccount(address, _blockTree.Head.StateRoot);
        }

        public Account GetAccount(Address address, Keccak stateRoot)
        {
            return _stateReader.GetAccount(stateRoot, address);
        }

        public int GetNetworkId() => _blockTree.ChainId;
        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);
        public FilterLog[] GetFilterLogs(int filterId) => _filterManager.GetLogs(filterId);

        public FilterLog[] GetLogs(FilterBlock fromBlock, FilterBlock toBlock, object address = null,
            IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return new FilterLog[0];
        }

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)	
        {	
            _stateReader.RunTreeVisitor(stateRoot, treeVisitor);	
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

        public int NewPendingTransactionFilter()
        {
            PendingTransactionFilter filter = _filterStore.CreatePendingTransactionFilter();
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public void UninstallFilter(int filterId) => _filterStore.RemoveFilter(filterId);
        public FilterLog[] GetLogFilterChanges(int filterId) => _filterManager.PollLogs(filterId);
        public Keccak[] GetBlockFilterChanges(int filterId) => _filterManager.PollBlockHashes(filterId);

        public void RecoverTxSenders(Block block)
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                RecoverTxSender(block.Transactions[i], block.Number);
            }
        }

        public void RecoverTxSender(Transaction tx, long blockNumber)
        {
            tx.SenderAddress = _ecdsa.RecoverAddress(tx, blockNumber);
        }
        
        public Keccak[] GetPendingTransactionFilterChanges(int filterId) =>
            _filterManager.PollPendingTransactionHashes(filterId);
    }
}