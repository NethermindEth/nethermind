// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization.ParallelSync;
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
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.State;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Transaction = Nethermind.Core.Transaction;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Facade
{
    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
    public class BlockchainBridge(
        IOverridableEnv<BlockchainBridge.BlockProcessingComponents> processingEnv,
        Lazy<ISimulateReadOnlyBlocksProcessingEnv> lazySimulateProcessingEnv,
        IBlockTree blockTree,
        IStateReader stateReader,
        ITxPool txPool,
        IReceiptFinder receiptStorage,
        IFilterStore filterStore,
        IFilterManager filterManager,
        IEthereumEcdsa ecdsa,
        ITimestamper timestamper,
        ILogFinder logFinder,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        IMiningConfig miningConfig,
        IPruningConfig pruningConfig,
        IEthSyncingInfo ethSyncingInfo)
        : IBlockchainBridge
    {
        private readonly SimulateBridgeHelper _simulateBridgeHelper = new(blocksConfig, specProvider);

        public Block? HeadBlock
        {
            get
            {
                return blockTree.Head;
            }
        }

        public bool IsMining { get; } = miningConfig.Enabled;

        private bool TryGetCanonicalTransaction(
            Hash256 txHash,
            [NotNullWhen(true)] out Transaction? transaction,
            [NotNullWhen(true)] out TxReceipt? receipt,
            [NotNullWhen(true)] out Block? block,
            [NotNullWhen(true)] out TxReceipt[]? receipts)
        {
            Hash256 blockHash = receiptStorage.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null)
                {
                    receipts = receiptStorage.Get(block);
                    int txIndex = block.GetTransactionIndex(txHash.ValueHash256);
                    if (txIndex != -1)
                    {
                        transaction = block.Transactions[txIndex];
                        receipt = receipts.Length > txIndex && receipts[txIndex].TxHash == txHash ? receipts[txIndex] : null;
                        return true;
                    }
                }
            }

            transaction = null;
            receipt = null;
            receipts = null;
            block = null;
            return false;
        }

        public (TxReceipt? Receipt, ulong BlockTimestamp, TxGasInfo? GasInfo, int LogIndexStart) GetTxReceiptInfo(Hash256 txHash)
        {
            if (TryGetCanonicalTransaction(txHash, out Transaction? tx, out TxReceipt? txReceipt, out Block? block, out TxReceipt[]? txReceipts))
            {
                int logIndexStart = txReceipts.GetBlockLogFirstIndex(txReceipt.Index);
                IReleaseSpec spec = specProvider.GetSpec(block.Header);
                return (txReceipt, block.Timestamp, tx.GetGasInfo(spec, block.Header), logIndexStart);
            }

            return (null, 0, null, 0);
        }

        public (TxReceipt? Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Hash256 txHash, bool checkTxnPool = true) =>
            TryGetCanonicalTransaction(txHash, out Transaction? tx, out TxReceipt? txReceipt, out Block? block, out TxReceipt[]? _)
                ? (txReceipt, tx, block.BaseFeePerGas)
                : checkTxnPool && txPool.TryGetPendingTransaction(txHash, out Transaction? transaction)
                    ? (null, transaction, null)
                    : (null, null, null);

        public TxReceipt? GetReceipt(Hash256 txHash)
        {
            Hash256? blockHash = receiptStorage.FindBlockHash(txHash);
            return blockHash is not null ? receiptStorage.Get(blockHash).ForTransaction(txHash) : null;
        }

        public CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken cancellationToken)
        {
            using var scope = processingEnv.BuildAndOverride(header, stateOverride);

            CallOutputTracer callOutputTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(scope.Component, header, tx, false,
                callOutputTracer.WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = ConstructError(tryCallResult, callOutputTracer.Error),
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.TransactionExecuted
            };
        }

        public SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, long gasCapLimit, CancellationToken cancellationToken)
        {
            using SimulateReadOnlyBlocksProcessingScope env = lazySimulateProcessingEnv.Value.Begin(header);
            env.SimulateRequestState.Validate = payload.Validation;
            IBlockTracer<TTrace> tracer = simulateBlockTracerFactory.CreateSimulateBlockTracer(payload.TraceTransfers, env.WorldState, specProvider, header);
            return _simulateBridgeHelper.TrySimulate(header, payload, tracer, env, gasCapLimit, cancellationToken);
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMargin, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken cancellationToken)
        {
            using var scope = processingEnv.BuildAndOverride(header, stateOverride);
            var components = scope.Component;

            EstimateGasTracer estimateGasTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(components, header, tx, true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(components.TransactionProcessor, components.WorldState, specProvider, blocksConfig);

            string? error = ConstructError(tryCallResult, estimateGasTracer.Error);

            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer, out string? err, errorMargin, cancellationToken);
            if (err is not null)
            {
                error ??= err;
            }

            return new CallOutput
            {
                Error = error,
                GasSpent = estimate,
                InputError = !tryCallResult.TransactionExecuted || err is not null
            };
        }

        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize)
        {
            AccessTxTracer accessTxTracer = optimize
                ? new(tx.SenderAddress,
                    tx.GetRecipient(tx.IsContractCreation ? stateReader.GetNonce(header, tx.SenderAddress) : 0), header.GasBeneficiary)
                : new(header.GasBeneficiary);

            CallOutputTracer callOutputTracer = new();

            using var scope = processingEnv.BuildAndOverride(header);
            var components = scope.Component;

            TransactionResult tryCallResult = TryCallAndRestore(components, header, tx, false,
                new CompositeTxTracer(callOutputTracer, accessTxTracer).WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = ConstructError(tryCallResult, callOutputTracer.Error),
                GasSpent = accessTxTracer.GasSpent,
                OperationGas = callOutputTracer.OperationGas,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.TransactionExecuted,
                AccessList = accessTxTracer.AccessList
            };
        }

        private TransactionResult TryCallAndRestore(
            BlockProcessingComponents components,
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                return CallAndRestore(blockHeader, transaction, treatBlockHeaderAsParentBlock, tracer, components);
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
            BlockProcessingComponents components)
        {
            transaction.SenderAddress ??= Address.SystemUser;

            //Ignore nonce on all CallAndRestore calls
            transaction.Nonce = components.StateReader.GetNonce(blockHeader, transaction.SenderAddress);

            BlockHeader callHeader = treatBlockHeaderAsParentBlock
                ? new(
                    blockHeader.Hash!,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    UInt256.Zero,
                    blockHeader.Number + 1,
                    blockHeader.GasLimit,
                    Math.Max(blockHeader.Timestamp + blocksConfig.SecondsPerSlot, timestamper.UnixTime.Seconds),
                    [])
                : new(
                    blockHeader.ParentHash!,
                    blockHeader.UnclesHash!,
                    blockHeader.Beneficiary!,
                    blockHeader.Difficulty,
                    blockHeader.Number,
                    blockHeader.GasLimit,
                    blockHeader.Timestamp,
                    blockHeader.ExtraData);

            IReleaseSpec releaseSpec = specProvider.GetSpec(callHeader);
            callHeader.BaseFeePerGas = treatBlockHeaderAsParentBlock
                ? BaseFeeCalculator.Calculate(blockHeader, releaseSpec)
                : blockHeader.BaseFeePerGas;

            UInt256 blobBaseFee = UInt256.Zero;

            if (releaseSpec.IsEip4844Enabled)
            {
                callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(transaction);
                callHeader.ExcessBlobGas = treatBlockHeaderAsParentBlock
                    ? BlobGasCalculator.CalculateExcessBlobGas(blockHeader, releaseSpec)
                    : blockHeader.ExcessBlobGas;

                if (transaction.Type is TxType.Blob && transaction.MaxFeePerBlobGas is null && BlobGasCalculator.TryCalculateFeePerBlobGas(callHeader, releaseSpec.BlobBaseFeeUpdateFraction, out blobBaseFee))
                {
                    transaction.MaxFeePerBlobGas = blobBaseFee;
                }
            }
            callHeader.MixHash = blockHeader.MixHash;
            callHeader.IsPostMerge = blockHeader.Difficulty == 0;
            transaction.Hash = transaction.CalculateHash();
            BlockExecutionContext blockExecutionContext = new(callHeader, releaseSpec, blobBaseFee);
            return components.TransactionProcessor.CallAndRestore(transaction, in blockExecutionContext, tracer);
        }

        public ulong GetChainId()
        {
            return blockTree.ChainId;
        }

        public bool FilterExists(int filterId) => filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => filterStore.GetFilterType(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null,
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = GetFilter(fromBlock, toBlock, address, topics);
            return logFinder.FindLogs(filter, cancellationToken);
        }

        public LogFilter GetFilter(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null)
        {
            return filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
        }

        public IEnumerable<FilterLog> GetLogs(
            LogFilter filter,
            BlockHeader fromBlock,
            BlockHeader toBlock,
            CancellationToken cancellationToken = default)
        {
            return logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        public bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default)
        {
            LogFilter? filter;
            filterLogs = null;
            if ((filter = filterStore.GetFilter<LogFilter>(filterId)) is not null)
                filterLogs = logFinder.FindLogs(filter, cancellationToken);

            return filter is not null;
        }

        public int NewFilter(BlockParameter? fromBlock, BlockParameter? toBlock,
            object? address = null, IEnumerable<object>? topics = null)
        {
            LogFilter filter = filterStore.CreateLogFilter(fromBlock ?? BlockParameter.Latest, toBlock ?? BlockParameter.Latest, address, topics);
            filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = filterStore.CreateBlockFilter(blockTree.Head!.Number);
            filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewPendingTransactionFilter()
        {
            PendingTransactionFilter filter = filterStore.CreatePendingTransactionFilter();
            filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public void UninstallFilter(int filterId) => filterStore.RemoveFilter(filterId);
        public FilterLog[] GetLogFilterChanges(int filterId) => filterManager.PollLogs(filterId);
        public Hash256[] GetBlockFilterChanges(int filterId) => filterManager.PollBlockHashes(filterId);

        public void RecoverTxSenders(Block block)
        {
            TxReceipt[] receipts = receiptStorage.Get(block);
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
            filterManager.PollPendingTransactionHashes(filterId);

        public Address? RecoverTxSender(Transaction tx) => ecdsa.RecoverAddress(tx);

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot) where TCtx : struct, INodeContext<TCtx>
        {
            stateReader.RunTreeVisitor(treeVisitor, stateRoot);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock)
        {
            if (baseBlock is null)
            {
                return false;
            }

            // For archive nodes (no pruning), return true only when fully synced
            if (pruningConfig.Mode == PruningMode.None)
            {
                // Archive node should have all state, but only after initial sync is complete
                return !ethSyncingInfo.IsSyncing();
            }

            // For nodes with pruning enabled, check if we're still syncing state
            // Once state sync is complete (even if still downloading old bodies/receipts),
            // we have all state up to the pruning boundary
            if (ethSyncingInfo.SyncMode.HaveNotSyncedStateYet())
            {
                // Conservative: don't claim we have state during state sync
                // This prevents race conditions during sync process
                return false;
            }

            // Check if the requested block is within the pruning window
            // We subtract 1 from the pruning boundary as a safety margin to account for:
            // 1. Race conditions between checking state and accessing it
            // 2. Blocks that might be pruned between the check and actual access
            // This means we sacrifice 1 block of history but eliminate the race condition
            long headNumber = blockTree.Head?.Number ?? 0;
            long requestedNumber = baseBlock.Number;
            int pruningBoundary = pruningConfig.PruningBoundary;

            // Conservative check: subtract 1 from boundary to prevent returning true
            // for blocks that are at the edge of being pruned
            bool isWithinPruningWindow = (headNumber - requestedNumber) <= (pruningBoundary - 1);

            return isWithinPruningWindow;
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
        {
            return logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            return logFinder.FindLogs(filter, cancellationToken);
        }

        private static string? ConstructError(TransactionResult txResult, string? tracerError)
        {
            var error = txResult switch
            {
                { TransactionExecuted: true } when txResult.EvmExceptionType is not EvmExceptionType.None => txResult.EvmExceptionType.GetEvmExceptionDescription(),
                { TransactionExecuted: true } when tracerError is not null => tracerError,
                { TransactionExecuted: false, Error: not null } => txResult.Error,
                _ => null
            };

            return error;
        }

        public record BlockProcessingComponents(
            IStateReader StateReader,
            ITransactionProcessor TransactionProcessor,
            IWorldState WorldState
        );
    }

    public interface IBlockchainBridgeFactory
    {
        IBlockchainBridge CreateBlockchainBridge();
    }

    public class BlockchainBridgeFactory(
        ISimulateReadOnlyBlocksProcessingEnvFactory simulateEnvFactory,
        IOverridableEnvFactory envFactory,
        ILifetimeScope rootLifetimeScope
    ) : IBlockchainBridgeFactory
    {
        public IBlockchainBridge CreateBlockchainBridge()
        {
            IOverridableEnv env = envFactory.Create();

            ILifetimeScope overridableScopeLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddModule(env)
                .Add<BlockchainBridge.BlockProcessingComponents>());

            // Split it out to isolate the world state and processing components
            IOverridableEnv<BlockchainBridge.BlockProcessingComponents> blockProcessingEnv = overridableScopeLifetime
                .Resolve<IOverridableEnv<BlockchainBridge.BlockProcessingComponents>>();

            ILifetimeScope blockchainBridgeLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddScoped<BlockchainBridge>()
                .AddScoped<ISimulateReadOnlyBlocksProcessingEnv>((_) => simulateEnvFactory.Create())
                .AddScoped(blockProcessingEnv));

            blockchainBridgeLifetime.Disposer.AddInstanceForAsyncDisposal(overridableScopeLifetime);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(blockchainBridgeLifetime);

            return blockchainBridgeLifetime.Resolve<BlockchainBridge>();
        }
    }
}
