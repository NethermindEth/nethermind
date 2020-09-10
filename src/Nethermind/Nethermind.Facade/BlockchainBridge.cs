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
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Trie;
using Nethermind.TxPool;
using Block = Nethermind.Core.Block;
using System.Threading;
using Nethermind.Blockchain.Processing;

namespace Nethermind.Facade
{
    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly ReadOnlyTxProcessingEnv _processingEnv;
        private readonly ITxPool _txPool;
        private readonly IBlockTree _blockTree;
        private readonly IFilterStore _filterStore;
        private readonly IStateReader _stateReader;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITimestamper _timestamper;
        private readonly IFilterManager _filterManager;
        private readonly IStateProvider _stateProvider;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ILogFinder _logFinder;

        public BlockchainBridge(
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool txPool,
            IReceiptFinder receiptStorage,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IEthereumEcdsa ecdsa,
            IBloomStorage bloomStorage,
            ITimestamper timestamper,
            ILogManager logManager,
            bool isMining,
            int findLogBlockDepthLimit = 1000,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            _processingEnv = processingEnv ?? throw new ArgumentNullException(nameof(processingEnv));
            _stateReader = processingEnv.StateReader ?? throw new ArgumentNullException(nameof(processingEnv.StateReader));
            _stateProvider = processingEnv.StateProvider ?? throw new ArgumentNullException(nameof(processingEnv.StateProvider));
            _blockTree = processingEnv.BlockTree ?? throw new ArgumentNullException(nameof(processingEnv.BlockTree));
            _transactionProcessor = processingEnv.TransactionProcessor ?? throw new ArgumentException(nameof(processingEnv.TransactionProcessor));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptFinder = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentException(nameof(filterManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            IsMining = isMining;
            _logFinder = new LogFinder(_blockTree, _receiptFinder, bloomStorage, logManager, new ReceiptsRecovery(), findLogBlockDepthLimit);
        }
        
        public Block BeamHead
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
            CallAndRestore(blockHeader, blockHeader.Number, blockHeader.Timestamp,  transaction, callOutputTracer);
            return new CallOutput
            {
                Error = callOutputTracer.Error,
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue
            };
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            EstimateGasTracer estimateGasTracer = new EstimateGasTracer(cancellationToken);
            CallAndRestore(
                header,
                header.Number + 1,
                UInt256.Max(header.Timestamp + 1, _timestamper.EpochSeconds),
                tx,
                estimateGasTracer);
            
            long estimate = estimateGasTracer.CalculateEstimate(tx);
            return new CallOutput {Error = estimateGasTracer.Error, GasSpent = estimate};
        }

        private void CallAndRestore(
            BlockHeader blockHeader,
            long number,
            UInt256 timestamp,
            Transaction transaction,
            ITxTracer tracer)
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
                    number,
                    blockHeader.GasLimit,
                    timestamp,
                    Array.Empty<byte>());

                transaction.Hash = transaction.CalculateHash();
                _transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
            }
            finally
            {
                _processingEnv.Reset();
            }
        }

        public long GetChainId()
        {
            return _blockTree.ChainId;
        }
        
        private UInt256 GetNonce(Keccak stateRoot, Address address)
        {
            return _stateReader.GetNonce(stateRoot, address);
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
    }
}