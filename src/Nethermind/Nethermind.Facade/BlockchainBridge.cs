// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Transaction = Nethermind.Core.Transaction;
using System.Linq;

namespace Nethermind.Facade
{
    public interface IBlockchainBridgeFactory
    {
        IBlockchainBridge CreateBlockchainBridge();
    }

    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IOverridableTxProcessorSource _processingEnv;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ITxPool _txPool;
        private readonly IFilterStore _filterStore;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITimestamper _timestamper;
        private readonly IFilterManager _filterManager;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ILogFinder _logFinder;
        private readonly ISpecProvider _specProvider;
        private readonly IBlocksConfig _blocksConfig;
        private readonly SimulateBridgeHelper _simulateBridgeHelper;
        private readonly SimulateReadOnlyBlocksProcessingEnvFactory _simulateProcessingEnvFactory;

        public BlockchainBridge(OverridableTxProcessingEnv processingEnv,
            SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory,
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
            _blockTree = processingEnv.BlockTree;
            _stateReader = processingEnv.StateReader;
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
            _simulateProcessingEnvFactory = simulateProcessingEnvFactory ?? throw new ArgumentNullException(nameof(simulateProcessingEnvFactory));
            _simulateBridgeHelper = new SimulateBridgeHelper(_blocksConfig, _specProvider);
        }

        public Block? HeadBlock
        {
            get
            {
                return _blockTree.Head;
            }
        }

        public bool IsMining { get; }

        private bool TryGetCanonicalTransaction(
            Hash256 txHash,
            [NotNullWhen(true)] out Transaction? transaction,
            [NotNullWhen(true)] out TxReceipt? receipt,
            [NotNullWhen(true)] out Block? block,
            [NotNullWhen(true)] out TxReceipt[]? receipts)
        {
            Hash256 blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null)
                {
                    receipts = _receiptFinder.Get(block);
                    receipt = receipts.ForTransaction(txHash);
                    transaction = block.Transactions[receipt.Index];
                    return true;
                }
            }

            transaction = null;
            receipt = null;
            receipts = null;
            block = null;
            return false;
        }

        public (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) GetReceiptAndGasInfo(Hash256 txHash)
        {
            if (TryGetCanonicalTransaction(txHash, out Transaction? tx, out TxReceipt? txReceipt, out Block? block, out TxReceipt[]? txReceipts))
            {
                int logIndexStart = txReceipts.GetBlockLogFirstIndex(txReceipt.Index);
                IReleaseSpec spec = _specProvider.GetSpec(block.Header);
                return (txReceipt, tx.GetGasInfo(spec, block.Header), logIndexStart);
            }

            return (null, null, 0);
        }

        public (TxReceipt? Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Hash256 txHash, bool checkTxnPool = true) =>
            TryGetCanonicalTransaction(txHash, out Transaction? tx, out TxReceipt? txReceipt, out Block? block, out TxReceipt[]? _)
                ? (txReceipt, tx, block.BaseFeePerGas)
                : checkTxnPool && _txPool.TryGetPendingTransaction(txHash, out Transaction? transaction)
                    ? (null, transaction, null)
                    : (null, null, null);

        public TxReceipt? GetReceipt(Hash256 txHash)
        {
            Hash256? blockHash = _receiptFinder.FindBlockHash(txHash);
            return blockHash is not null ? _receiptFinder.Get(blockHash).ForTransaction(txHash) : null;
        }

        public CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken cancellationToken)
        {
            using IOverridableTxProcessingScope scope = _processingEnv.BuildAndOverride(header, stateOverride);

            CallOutputTracer callOutputTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(scope, header, tx, false,
                callOutputTracer.WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = ConstructError(tryCallResult, callOutputTracer.Error, tx.GasLimit),
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success
            };
        }

        public SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, CancellationToken cancellationToken)
        {
            SimulateReadOnlyBlocksProcessingEnv env = _simulateProcessingEnvFactory.Create(payload.Validation);
            IBlockTracer<TTrace> tracer = simulateBlockTracerFactory.CreateSimulateBlockTracer(payload.TraceTransfers, env.WorldState, _specProvider, header);
            return _simulateBridgeHelper.TrySimulate(header, payload, tracer, env, cancellationToken);
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMargin, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken cancellationToken)
        {
            using IOverridableTxProcessingScope scope = _processingEnv.BuildAndOverride(header, stateOverride);

            EstimateGasTracer estimateGasTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(scope, header, tx, true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(scope.TransactionProcessor, scope.WorldState, _specProvider, _blocksConfig);

            string? error = ConstructError(tryCallResult, estimateGasTracer.Error, tx.GasLimit);

            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer, out string? err, errorMargin, cancellationToken);
            if (err is not null)
            {
                error ??= err;
            }

            return new CallOutput
            {
                Error = error,
                GasSpent = estimate,
                InputError = !tryCallResult.Success || err is not null
            };
        }

        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize)
        {
            AccessTxTracer accessTxTracer = optimize
                ? new(tx.SenderAddress,
                    tx.GetRecipient(tx.IsContractCreation ? _stateReader.GetNonce(header.StateRoot, tx.SenderAddress) : 0), header.GasBeneficiary)
                : new(header.GasBeneficiary);

            CallOutputTracer callOutputTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(_processingEnv.Build(header.StateRoot!), header, tx, false,
                new CompositeTxTracer(callOutputTracer, accessTxTracer).WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = ConstructError(tryCallResult, callOutputTracer.Error, tx.GasLimit),
                GasSpent = accessTxTracer.GasSpent,
                OperationGas = callOutputTracer.OperationGas,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success,
                AccessList = accessTxTracer.AccessList
            };
        }

        private TransactionResult TryCallAndRestore(
            IOverridableTxProcessingScope scope,
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                return CallAndRestore(blockHeader, transaction, treatBlockHeaderAsParentBlock, tracer, scope);
            }
            catch (InsufficientBalanceException ex)
            {
                return new TransactionResult(ex.Message);
            }
        }

        private TransactionResult CallAndRestore(
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer,
            IOverridableTxProcessingScope scope)
        {
            transaction.SenderAddress ??= Address.SystemUser;
            Hash256 stateRoot = blockHeader.StateRoot!;

            if (transaction.Nonce == 0)
            {
                transaction.Nonce = GetNonce(stateRoot, transaction.SenderAddress);
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

                if (transaction.Type is TxType.Blob && transaction.MaxFeePerBlobGas is null && BlobGasCalculator.TryCalculateFeePerBlobGas(callHeader, releaseSpec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee))
                {
                    transaction.MaxFeePerBlobGas = blobBaseFee;
                }
            }
            callHeader.MixHash = blockHeader.MixHash;
            callHeader.IsPostMerge = blockHeader.Difficulty == 0;
            transaction.Hash = transaction.CalculateHash();
            return scope.TransactionProcessor.CallAndRestore(transaction, new(callHeader, releaseSpec.BlobBaseFeeUpdateFraction), tracer);
        }

        public ulong GetChainId()
        {
            return _blockTree.ChainId;
        }

        private UInt256 GetNonce(Hash256 stateRoot, Address address)
        {
            return _stateReader.GetNonce(stateRoot, address);
        }

        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null,
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = GetFilter(fromBlock, toBlock, address, topics);
            return _logFinder.FindLogs(filter, cancellationToken);
        }

        public LogFilter GetFilter(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null)
        {
            return _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
        }

        public IEnumerable<FilterLog> GetLogs(
            LogFilter filter,
            BlockHeader fromBlock,
            BlockHeader toBlock,
            CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
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
            BlockFilter filter = _filterStore.CreateBlockFilter(_blockTree.Head!.Number);
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
        public Hash256[] GetBlockFilterChanges(int filterId) => _filterManager.PollBlockHashes(filterId);

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

        public Hash256[] GetPendingTransactionFilterChanges(int filterId) =>
            _filterManager.PollPendingTransactionHashes(filterId);

        public Address? RecoverTxSender(Transaction tx) => _ecdsa.RecoverAddress(tx);

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot) where TCtx : struct, INodeContext<TCtx>
        {
            _stateReader.RunTreeVisitor(treeVisitor, stateRoot);
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            return _stateReader.HasStateForRoot(stateRoot);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, cancellationToken);
        }

        private static string? ConstructError(TransactionResult txResult, string? tracerError, long gasLimit)
        {
            var error = txResult switch
            {
                { Success: true } when tracerError is not null => tracerError,
                { Success: false, Error: not null } => txResult.Error,
                _ => null
            };

            return error is null ? null : $"err: {error} (supplied gas {gasLimit})";
        }
    }
}
