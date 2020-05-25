//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
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
        private readonly IReceiptFinder _receiptFinder;
        private readonly IStorageProvider _storageProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ILogFinder _logFinder;

        public BlockchainBridge(IStateReader stateReader,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            IBlockTree blockTree,
            ITxPool txPool,
            IReceiptFinder receiptStorage,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IWallet wallet,
            ITransactionProcessor transactionProcessor,
            IEthereumEcdsa ecdsa,
            IBloomStorage bloomStorage,
            ILogManager logManager,
            bool isMining,
            int findLogBlockDepthLimit = 1000)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptFinder = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentException(nameof(filterManager));
            _wallet = wallet ?? throw new ArgumentException(nameof(wallet));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentException(nameof(transactionProcessor));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            IsMining = isMining;

            _logFinder = new LogFinder(_blockTree, _receiptFinder, bloomStorage, logManager, new ReceiptsRecovery(), findLogBlockDepthLimit);
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

        public Block Head
        {
            get
            {
                bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;

                /*
                 * when we are in the process of synchronising state
                 * head remains Genesis block
                 * and we want to allow users to use the API
                 */
                return headIsGenesis ? _blockTree.BestSuggestedBody : _blockTree.Head;
            }
        }

        public long BestKnown => _blockTree.BestKnownNumber;

        public bool IsSyncing => _blockTree.BestSuggestedHeader.Hash != _blockTree.Head.Hash;
        
        public bool IsMining { get; }

        public (TxReceipt Receipt, Transaction Transaction) GetTransaction(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash != null)
            {
                Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
                return (txReceipt, block?.Transactions[txReceipt.Index]);
            }

            if (_txPool.TryGetPendingTransaction(txHash, out var transaction))
            {
                return (null, transaction);
            }

            return (null, null);
        }

        public TxReceipt GetReceipt(Keccak txHash)
        {
            var blockHash = _receiptFinder.FindBlockHash(txHash);
            return blockHash != null ? _receiptFinder.Get(blockHash).ForTransaction(txHash) : null;
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
            CallOutputTracer callOutputTracer = new CallOutputTracer();
            CallAndRestore(blockHeader, transaction, callOutputTracer);
            return new CallOutput {Error = callOutputTracer.Error, GasSpent = callOutputTracer.GasSpent, OutputData = callOutputTracer.ReturnValue};
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx)
        {
            EstimateGasTracer estimateGasTracer = new EstimateGasTracer();
            CallAndRestore(header, tx, estimateGasTracer);
            long estimate = estimateGasTracer.CalculateEstimate(tx);
            return new CallOutput {Error = estimateGasTracer.Error, GasSpent = estimate};
        }

        private void CallAndRestore(BlockHeader blockHeader, Transaction transaction, ITxTracer tracer)
        {
            if (transaction.SenderAddress == null)
            {
                transaction.SenderAddress = Address.SystemUser;
            }

            _stateProvider.StateRoot = blockHeader.StateRoot;
            try
            {
                if (transaction.Nonce == 0)
                {
                    transaction.Nonce = GetNonce(_stateProvider.StateRoot, transaction.SenderAddress);
                }

                BlockHeader callHeader = new BlockHeader(
                    blockHeader.Hash,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    0,
                    blockHeader.Number + 1,
                    blockHeader.GasLimit,
                    blockHeader.Timestamp,
                    Bytes.Empty);

                transaction.Hash = transaction.CalculateHash();
                _transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
            }
            finally
            {
                _stateProvider.Reset();
                _storageProvider.Reset();
            }
        }

        public long GetChainId()
        {
            return _blockTree.ChainId;
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
            return GetNonce(_blockTree.Head.StateRoot, address);
        }

        private UInt256 GetNonce(Keccak stateRoot, Address address)
        {
            return _stateReader.GetNonce(stateRoot, address);
        }

        public byte[] GetStorage(Address address, UInt256 index, Keccak stateRoot)
        {
            _stateProvider.StateRoot = stateRoot;
            return _storageProvider.Get(new StorageCell(address, index));
        }

        public Account GetAccount(Address address, Keccak stateRoot)
        {
            return _stateReader.GetAccount(stateRoot, address);
        }

        public int GetNetworkId() => _blockTree.ChainId;
        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);
        public FilterLog[] GetFilterLogs(int filterId) => _filterManager.GetLogs(filterId);

        public IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, object address = null,
            IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return _logFinder.FindLogs(filter);
        }

        public int NewFilter(BlockParameter fromBlock, BlockParameter toBlock,
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
                var transaction = block.Transactions[i];
                if (transaction.SenderAddress == null)
                {
                    RecoverTxSender(transaction);
                }
            }
        }

        public Keccak[] GetPendingTransactionFilterChanges(int filterId) =>
            _filterManager.PollPendingTransactionHashes(filterId);

        public void RecoverTxSender(Transaction tx)
        {
            tx.SenderAddress = _ecdsa.RecoverAddress(tx);
        }

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)
        {
            _stateReader.RunTreeVisitor(treeVisitor, stateRoot);
        }

        public Keccak HeadHash => _blockTree.HeadHash;
        public Keccak GenesisHash => _blockTree.GenesisHash;
        public Keccak PendingHash => _blockTree.PendingHash;
        public Block FindBlock(Keccak blockHash, BlockTreeLookupOptions options) => _blockTree.FindBlock(blockHash, options);
        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options) => _blockTree.FindBlock(blockNumber, options);
        public BlockHeader FindHeader(Keccak blockHash, BlockTreeLookupOptions options) => _blockTree.FindHeader(blockHash, options);
        public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) => _blockTree.FindHeader(blockNumber, options);
        public Keccak FindBlockHash(long blockNumber) => _blockTree.FindBlockHash(blockNumber);

        public bool IsMainChain(BlockHeader blockHeader) => _blockTree.IsMainChain(blockHeader);
        public bool IsMainChain(Keccak blockHash) => _blockTree.IsMainChain(blockHash);
    }
}