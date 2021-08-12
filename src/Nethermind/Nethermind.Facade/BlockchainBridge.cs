//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Trie;
using Nethermind.TxPool;
using Block = Nethermind.Core.Block;
using System.Threading;
using Nethermind.Blockchain.Processing;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Facade
{
    public interface IBlockchainBridgeFactory
    {
        IBlockchainBridge CreateBlockchainBridge();
    }
    
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
        private readonly bool _isBeamSyncing;
        private readonly IFilterManager _filterManager;
        private readonly IStateProvider _stateProvider;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ILogFinder _logFinder;
        private readonly ISpecProvider _specProvider;

        public BlockchainBridge(
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool? txPool,
            IReceiptFinder? receiptStorage,
            IFilterStore? filterStore,
            IFilterManager? filterManager,
            IEthereumEcdsa? ecdsa,
            ITimestamper? timestamper,
            ILogFinder? logFinder,
            ISpecProvider specProvider,
            bool isMining,
            bool isBeamSyncing)
        {
            _processingEnv = processingEnv ?? throw new ArgumentNullException(nameof(processingEnv));
            _stateReader = processingEnv.StateReader ?? throw new ArgumentNullException(nameof(processingEnv.StateReader));
            _stateProvider = processingEnv.StateProvider ?? throw new ArgumentNullException(nameof(processingEnv.StateProvider));
            _blockTree = processingEnv.BlockTree ?? throw new ArgumentNullException(nameof(processingEnv.BlockTree));
            _transactionProcessor = processingEnv.TransactionProcessor ?? throw new ArgumentNullException(nameof(processingEnv.TransactionProcessor));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptFinder = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentNullException(nameof(filterManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _isBeamSyncing = isBeamSyncing;
            IsMining = isMining;
        }
        
        public Block BeamHead
        {
            get
            {
                Block result;
                if (_isBeamSyncing)
                {
                    bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;

                    /*
                     * when we are in the process of synchronising state in beam sync
                     * head remains Genesis block
                     * and we want to allow users to use the API
                     */
                    result = headIsGenesis ? _blockTree.BestSuggestedBody : _blockTree.Head;
                }
                else
                {
                    result = _blockTree.Head;    
                }
                
                return result;
            }
        }

        public bool IsMining { get; }

        public (TxReceipt Receipt, UInt256? EffectiveGasPrice) GetReceiptAndEffectiveGasPrice(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash != null)
            {
                Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
                Transaction tx = block?.Transactions[txReceipt.Index];
                bool is1559Enabled = _specProvider.GetSpec(block.Number).IsEip1559Enabled;
                UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(is1559Enabled, block.Header.BaseFeePerGas);
                return (txReceipt, effectiveGasPrice);
            }

            return (null, null);
        }

        public (TxReceipt Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash != null)
            {
                Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
                return (txReceipt, block?.Transactions[txReceipt.Index], block?.BaseFeePerGas);
            }

            if (_txPool.TryGetPendingTransaction(txHash, out Transaction? transaction))
            {
                return (null, transaction, null);
            }

            return (null, null, null);
        }

        public TxReceipt GetReceipt(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            return blockHash != null ? _receiptFinder.Get(blockHash).ForTransaction(txHash) : null;
        }

        public class CallOutput
        {
            public CallOutput()
            {
            }

            public CallOutput(byte[] outputData, long gasSpent, string error, bool inputError = false)
            {
                Error = error;
                OutputData = outputData;
                GasSpent = gasSpent;
                InputError = inputError;
            }

            public string? Error { get; set; }

            public byte[] OutputData { get; set; }

            public long GasSpent { get; set; }
            
            public bool InputError { get; set; }
            
            public AccessList? AccessList { get; set; }
        }

        public CallOutput Call(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            CallOutputTracer callOutputTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, header.Timestamp, tx, false,
                callOutputTracer.WithCancellation(cancellationToken));
            return new CallOutput
            {
                Error = tryCallResult.Success ? callOutputTracer.Error : tryCallResult.Error,
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success
            };
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            EstimateGasTracer estimateGasTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(
                header,
                UInt256.Max(header.Timestamp + 1, _timestamper.UnixTime.Seconds),
                tx,
                true,
                estimateGasTracer.WithCancellation(cancellationToken));
            
            long estimate = estimateGasTracer.CalculateEstimate(tx, _specProvider.GetSpec(header.Number + 1));
            
            return new CallOutput 
            {
                Error = tryCallResult.Success ? estimateGasTracer.Error : tryCallResult.Error, 
                GasSpent = estimate, 
                InputError = !tryCallResult.Success
            };
        }

        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize)
        {
            CallOutputTracer callOutputTracer = new();
            AccessTxTracer accessTxTracer = optimize 
                ? new(tx.SenderAddress, 
                    tx.GetRecipient(tx.IsContractCreation ? _stateReader.GetNonce(header.StateRoot, tx.SenderAddress) : 0)) 
                : new();

            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, header.Timestamp, tx, false,
                new CompositeTxTracer(callOutputTracer, accessTxTracer).WithCancellation(cancellationToken));
            
            return new CallOutput
            {
                Error = tryCallResult.Success ? callOutputTracer.Error : tryCallResult.Error,
                GasSpent = accessTxTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success,
                AccessList = accessTxTracer.AccessList
            };
        }

        private (bool Success, string Error) TryCallAndRestore(
            BlockHeader blockHeader,
            UInt256 timestamp,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                CallAndRestore(blockHeader, timestamp, transaction, treatBlockHeaderAsParentBlock, tracer);
                return (true, string.Empty);
            }
            catch (InsufficientBalanceException ex)
            {
                return (false, ex.Message);
            }
        }

        private void CallAndRestore(
            BlockHeader blockHeader,
            UInt256 timestamp,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
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

                BlockHeader callHeader = new(
                    blockHeader.Hash,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    0,
                    treatBlockHeaderAsParentBlock ? blockHeader.Number + 1 : blockHeader.Number,
                    blockHeader.GasLimit,
                    timestamp,
                    Array.Empty<byte>());

                callHeader.BaseFeePerGas = treatBlockHeaderAsParentBlock
                    ? BaseFeeCalculator.Calculate(blockHeader, _specProvider.GetSpec(callHeader.Number))
                    : blockHeader.BaseFeePerGas;

                transaction.Hash = transaction.CalculateHash();
                _transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
            }
            finally
            {
                _processingEnv.Reset();
            }
        }

        public ulong GetChainId()
        {
            return _blockTree.ChainId;
        }
        
        private UInt256 GetNonce(Keccak stateRoot, Address address)
        {
            return _stateReader.GetNonce(stateRoot, address);
        }

        public ulong GetNetworkId() => _blockTree.ChainId;
        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);
        public FilterLog[] GetFilterLogs(int filterId) => _filterManager.GetLogs(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock, 
            BlockParameter toBlock, 
            object address = null,
            IEnumerable<object> topics = null, 
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return _logFinder.FindLogs(filter, cancellationToken);
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
                Transaction transaction = block.Transactions[i];
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

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, cancellationToken);
        }
    }
}
