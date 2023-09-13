// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Filters;
using Nethermind.State;
using Nethermind.Core.Extensions;
using Nethermind.Config;
using Nethermind.Facade.Proxy.Models.MultiCall;
using System.Transactions;
using Microsoft.CSharp.RuntimeBinder;
using Transaction = Nethermind.Core.Transaction;
using Nethermind.Specs;

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
        private readonly IFilterStore _filterStore;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITimestamper _timestamper;
        private readonly IFilterManager _filterManager;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ILogFinder _logFinder;
        private readonly ISpecProvider _specProvider;
        private readonly IBlocksConfig _blocksConfig;
        private readonly MultycallBridgeHelper _multicallBridgeHelper;

        public BlockchainBridge(ReadOnlyTxProcessingEnv processingEnv,
            MultiCallReadOnlyBlocksProcessingEnv multiCallProcessingEnv,
            ITxPool? txPool,
            IReceiptFinder? receiptStorage,
            IFilterStore? filterStore,
            IFilterManager? filterManager,
            IEthereumEcdsa? ecdsa,
            ITimestamper? timestamper,
            ILogFinder? logFinder,
            ISpecProvider specProvider,
            IBlocksConfig blocksConfig,
            bool isMining)
        {
            _processingEnv = processingEnv ?? throw new ArgumentNullException(nameof(processingEnv));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptFinder = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentNullException(nameof(filterManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blocksConfig = blocksConfig;
            IsMining = isMining;
            _multicallBridgeHelper = new MultycallBridgeHelper(multiCallProcessingEnv ?? throw new ArgumentNullException(nameof(multiCallProcessingEnv)), _specProvider, _blocksConfig);
        }

        public Block? HeadBlock
        {
            get
            {
                return _processingEnv.BlockTree.Head;
            }
        }

        public bool IsMining { get; }

        public (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) GetReceiptAndGasInfo(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                Block? block = _processingEnv.BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null)
                {
                    TxReceipt[] txReceipts = _receiptFinder.Get(block);
                    TxReceipt txReceipt = txReceipts.ForTransaction(txHash);
                    int logIndexStart = txReceipts.GetBlockLogFirstIndex(txReceipt.Index);
                    Transaction tx = block.Transactions[txReceipt.Index];
                    bool is1559Enabled = _specProvider.GetSpecFor1559(block.Number).IsEip1559Enabled;
                    return (txReceipt, tx.GetGasInfo(is1559Enabled, block.Header), logIndexStart);
                }
            }

            return (null, null, 0);
        }

        public (TxReceipt? Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                Block block = _processingEnv.BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
                return (txReceipt, block?.Transactions[txReceipt.Index], block?.BaseFeePerGas);
            }

            if (_txPool.TryGetPendingTransaction(txHash, out Transaction? transaction))
            {
                return (null, transaction, null);
            }

            return (null, null, null);
        }

        public TxReceipt? GetReceipt(Keccak txHash)
        {
            Keccak? blockHash = _receiptFinder.FindBlockHash(txHash);
            return blockHash is not null ? _receiptFinder.Get(blockHash).ForTransaction(txHash) : null;
        }

        public CallOutput Call(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            CallOutputTracer callOutputTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, tx, false,
                callOutputTracer.WithCancellation(cancellationToken));
            return new CallOutput
            {
                Error = tryCallResult.Success ? callOutputTracer.Error : tryCallResult.Error,
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success
            };
        }

        public MultiCallOutput MultiCall(BlockHeader header, MultiCallPayload<Transaction> payload, CancellationToken cancellationToken)
        {
            MultiCallBlockTracer multiCallOutputTracer = new();
            MultiCallOutput result = new();
            try
            {
                (bool Success, string Error) tryMultiCallResult = _multicallBridgeHelper.TryMultiCallTrace(header, payload,
                    multiCallOutputTracer.WithCancellation(cancellationToken));

                if (!tryMultiCallResult.Success)
                {
                    result.Error = tryMultiCallResult.Error;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.ToString();
            }

            result.Items = multiCallOutputTracer.Results;
            return result;
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            using IReadOnlyTransactionProcessor? readOnlyTransactionProcessor = _processingEnv.Build(header.StateRoot!);

            EstimateGasTracer estimateGasTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(
                header,
                tx,
                true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(readOnlyTransactionProcessor, _processingEnv.StateProvider,
                _specProvider, _blocksConfig);
            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer, cancellationToken);

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
                    tx.GetRecipient(tx.IsContractCreation ? _processingEnv.StateReader.GetNonce(header.StateRoot, tx.SenderAddress) : 0))
                : new();

            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, tx, false,
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
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                CallAndRestore(blockHeader, transaction, treatBlockHeaderAsParentBlock, tracer);
                return (true, string.Empty);
            }
            catch (InsufficientBalanceException ex)
            {
                return (false, ex.Message);
            }
        }

        private void CallAndRestore(
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            transaction.SenderAddress ??= Address.SystemUser;

            Keccak stateRoot = blockHeader.StateRoot!;
            using IReadOnlyTransactionProcessor transactionProcessor = _processingEnv.Build(stateRoot);

            if (transaction.Nonce == 0)
            {
                try
                {
                    transaction.Nonce = _processingEnv.StateReader.GetNonce(stateRoot, transaction.SenderAddress);
                }
                catch (Exception)
                {
                    // TODO: handle missing state exception, may be account needs to be created
                }
            }

            BlockHeader callHeader = treatBlockHeaderAsParentBlock
                ? new(
                    blockHeader.Hash!,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    UInt256.Zero,
                    blockHeader.Number + 1,
                    blockHeader.GasLimit,
                    Math.Max(blockHeader.Timestamp + _blocksConfig.SecondsPerSlot, _timestamper.UnixTime.Seconds),
                    Array.Empty<byte>())
                : new(
                    blockHeader.ParentHash!,
                    blockHeader.UnclesHash!,
                    blockHeader.Beneficiary!,
                    blockHeader.Difficulty,
                    blockHeader.Number,
                    blockHeader.GasLimit,
                    blockHeader.Timestamp,
                    blockHeader.ExtraData);

            IReleaseSpec releaseSpec = _specProvider.GetSpec(callHeader);
            callHeader.BaseFeePerGas = treatBlockHeaderAsParentBlock
                ? BaseFeeCalculator.Calculate(blockHeader, releaseSpec)
                : blockHeader.BaseFeePerGas;

            if (releaseSpec.IsEip4844Enabled)
            {
                callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(transaction);
                callHeader.ExcessBlobGas = treatBlockHeaderAsParentBlock
                    ? BlobGasCalculator.CalculateExcessBlobGas(blockHeader, releaseSpec)
                    : blockHeader.ExcessBlobGas;
            }
            callHeader.MixHash = blockHeader.MixHash;
            callHeader.IsPostMerge = blockHeader.Difficulty == 0;
            transaction.Hash = transaction.CalculateHash();
            transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
        }


        public ulong GetChainId()
        {
            return _processingEnv.BlockTree.ChainId;
        }

        //Apply changes to accounts and contracts states including precompiles

        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);
        public FilterLog[] GetFilterLogs(int filterId) => _filterManager.GetLogs(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null,
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return _logFinder.FindLogs(filter, cancellationToken);
        }

        public bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default)
        {
            LogFilter? filter;
            filterLogs = null;
            if ((filter = _filterStore.GetFilter<LogFilter>(filterId)) is not null)
                filterLogs = _logFinder.FindLogs(filter, cancellationToken);

            return filter is not null;
        }

        public int NewFilter(BlockParameter? fromBlock, BlockParameter? toBlock,
            object? address = null, IEnumerable<object>? topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock ?? BlockParameter.Latest, toBlock ?? BlockParameter.Latest, address, topics);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = _filterStore.CreateBlockFilter(_processingEnv.BlockTree.Head!.Number);
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
            TxReceipt[] receipts = _receiptFinder.Get(block);
            if (block.Transactions.Length == receipts.Length)
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction transaction = block.Transactions[i];
                    TxReceipt receipt = receipts[i];
                    transaction.SenderAddress ??= receipt.Sender ?? RecoverTxSender(transaction);
                }
            }
            else
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction transaction = block.Transactions[i];
                    transaction.SenderAddress ??= RecoverTxSender(transaction);
                }
            }
        }

        public Keccak[] GetPendingTransactionFilterChanges(int filterId) =>
            _filterManager.PollPendingTransactionHashes(filterId);

        public Address? RecoverTxSender(Transaction tx) => _ecdsa.RecoverAddress(tx);

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)
        {
            _processingEnv.StateReader.RunTreeVisitor(treeVisitor, stateRoot);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, cancellationToken);
        }
    }
}
