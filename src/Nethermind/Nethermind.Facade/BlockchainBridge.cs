// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
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
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.State;
using Nethermind.Config;
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Transaction = Nethermind.Core.Transaction;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Eip2930;
using Nethermind.Consensus.Stateless;

namespace Nethermind.Facade
{
    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
    public class BlockchainBridge(
        IShareableOverridableEnvSource<BlockchainBridge.BlockProcessingComponents> processingEnv,
        IShareableTxProcessorSource shareableTxProcessorSource,
        Lazy<ISimulateReadOnlyBlocksProcessingEnv> lazySimulateProcessingEnv,
        Lazy<IWitnessGeneratingBlockProcessingEnvFactory> witnessGeneratingBlockProcessingEnvFactory,
        IBlockTree blockTree,
        IStateReader stateReader,
        ITxPool txPool,
        IReceiptFinder receiptStorage,
        FilterStore filterStore,
        FilterManager filterManager,
        IEthereumEcdsa ecdsa,
        ITimestamper timestamper,
        ILogFinder logFinder,
        IBlockAccessListStore balStore,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        IMiningConfig miningConfig)
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
                        return receipt is not null;
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

        public bool TryGetTransaction(Hash256 txHash, [NotNullWhen(true)] out TransactionLookupResult? result, bool checkTxnPool = true)
        {
            if (TryGetCanonicalTransaction(txHash, out Transaction? tx, out TxReceipt? txReceipt, out Block? block, out TxReceipt[]? _))
            {
                TransactionForRpcContext extraData = new(
                    chainId: specProvider.ChainId,
                    blockHash: block.Hash,
                    blockNumber: block.Number,
                    txIndex: txReceipt!.Index,
                    blockTimestamp: block.Timestamp,
                    baseFee: block.BaseFeePerGas,
                    receipt: txReceipt);


                result = new TransactionLookupResult(tx, extraData);
                return true;
            }

            if (checkTxnPool && txPool.TryGetPendingTransaction(txHash, out Transaction? transaction))
            {
                result = new TransactionLookupResult(transaction, new(specProvider.ChainId));
                return true;
            }

            result = null;
            return false;
        }

        public TxReceipt? GetReceipt(Hash256 txHash)
        {
            Hash256? blockHash = receiptStorage.FindBlockHash(txHash);
            return blockHash is not null ? receiptStorage.Get(blockHash).ForTransaction(txHash) : null;
        }

        public CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, UInt256? blobBaseFeeOverride, BlockOverride? blockOverride, CancellationToken cancellationToken) =>
            HasOverrides(stateOverride, blobBaseFeeOverride, blockOverride)
                ? CallExclusive(header, tx, stateOverride, blobBaseFeeOverride, blockOverride, cancellationToken)
                : CallShareable(header, tx, cancellationToken);

        private CallOutput CallShareable(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            using IReadOnlyTxProcessingScope scope = shareableTxProcessorSource.Build(header);
            return RunCall(stateReader, scope.TransactionProcessor, header, tx, blobBaseFeeOverride: null, cancellationToken);
        }

        private CallOutput CallExclusive(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, UInt256? blobBaseFeeOverride, BlockOverride? blockOverride, CancellationToken cancellationToken)
        {
            // BuildAndOverride opens the scope on the base block, applies the block override, and commits the
            // (possibly empty) override at the overridden block number — so the overridden header used below resolves.
            using Scope<BlockProcessingComponents> scope = processingEnv.BuildAndOverride(header, stateOverride, blockOverride);
            // Dual-write: RequestState feeds the VM-time decorator; RunCall applies it during pre-VM header prep.
            scope.Component.RequestState.BlobBaseFeeOverride = blobBaseFeeOverride;
            return RunCall(scope.Component.StateReader, scope.Component.TransactionProcessor, header, tx, blobBaseFeeOverride, cancellationToken);
        }

        private CallOutput RunCall(IStateReader nonceReader, ITransactionProcessor txProcessor, BlockHeader header, Transaction tx, UInt256? blobBaseFeeOverride, CancellationToken cancellationToken)
        {
            CallOutputTracer tracer = new();
            TransactionResult result = TryCallAndRestore(nonceReader, txProcessor, header, tx, treatBlockHeaderAsParentBlock: false,
                blobBaseFeeOverride, tracer.WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = result.GetErrorMessage(tracer.Error),
                GasSpent = tracer.GasSpent,
                OutputData = tracer.ReturnValue,
                InputError = !result.TransactionExecuted,
                ExecutionReverted = result.EvmExceptionType == EvmExceptionType.Revert,
            };
        }

        // Empty dict coalesces to no override: the exclusive env path is a free DoS vector for a no-op overlay.
        private static bool HasOverrides(Dictionary<Address, AccountOverride>? stateOverride, UInt256? blobBaseFeeOverride, BlockOverride? blockOverride) =>
            stateOverride is { Count: > 0 } || blobBaseFeeOverride is not null || blockOverride is not null;

        public SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, long gasCapLimit, CancellationToken cancellationToken)
        {
            using SimulateReadOnlyBlocksProcessingScope env = lazySimulateProcessingEnv.Value.Begin(header);
            env.SimulateRequestState.Validate = payload.Validation;
            IBlockTracer<TTrace> tracer = simulateBlockTracerFactory.CreateSimulateBlockTracer(payload.TraceTransfers, env.WorldState, env.SpecProvider, header);
            return _simulateBridgeHelper.TrySimulate(header, payload, tracer, env, gasCapLimit, cancellationToken);
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMargin, Dictionary<Address, AccountOverride>? stateOverride, UInt256? blobBaseFeeOverride, BlockOverride? blockOverride, CancellationToken cancellationToken) =>
            HasOverrides(stateOverride, blobBaseFeeOverride, blockOverride)
                ? EstimateGasExclusive(header, tx, errorMargin, stateOverride, blobBaseFeeOverride, blockOverride, cancellationToken)
                : EstimateGasShareable(header, tx, errorMargin, cancellationToken);

        private CallOutput EstimateGasShareable(BlockHeader header, Transaction tx, int errorMargin, CancellationToken cancellationToken)
        {
            using IReadOnlyTxProcessingScope scope = shareableTxProcessorSource.Build(header);
            return RunEstimateGas(stateReader, scope.TransactionProcessor, scope.WorldState, header, tx, errorMargin, blobBaseFeeOverride: null, cancellationToken);
        }

        private CallOutput EstimateGasExclusive(BlockHeader header, Transaction tx, int errorMargin, Dictionary<Address, AccountOverride>? stateOverride, UInt256? blobBaseFeeOverride, BlockOverride? blockOverride, CancellationToken cancellationToken)
        {
            using Scope<BlockProcessingComponents> scope = processingEnv.BuildAndOverride(header, stateOverride, blockOverride);
            BlockProcessingComponents components = scope.Component;
            components.RequestState.BlobBaseFeeOverride = blobBaseFeeOverride;
            return RunEstimateGas(components.StateReader, components.TransactionProcessor, components.WorldState, header, tx, errorMargin, blobBaseFeeOverride, cancellationToken);
        }

        private CallOutput RunEstimateGas(IStateReader nonceReader, ITransactionProcessor txProcessor, IWorldState worldState, BlockHeader header, Transaction tx, int errorMargin, UInt256? blobBaseFeeOverride, CancellationToken cancellationToken)
        {
            // Cap tx.GasLimit to the sender's affordable allowance before the initial probe,
            // mirroring Geth's hi = min(hi, (balance - value - blobFee) / gasFeeCap), where blobFee = 0 outside EIP-4844. This ensures
            // BuyGas never sees a gas limit that makes gasLimit * feeCap + blobFee exceed the sender's balance.
            IReleaseSpec spec = specProvider.GetSpec(header.Number + 1, header.Timestamp + blocksConfig.SecondsPerSlot);
            UInt256 senderBalance = worldState.GetBalance(tx.SenderAddress ?? Address.Zero);
            UInt256 feeCap = tx.CalculateFeeCap();
            if (feeCap > UInt256.Zero && !UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 availableForGas))
            {
                if (!BlobGasCalculator.TrySubtractBlobFee(spec, tx, ref availableForGas))
                    availableForGas = UInt256.Zero;

                long allowance = (long)UInt256.Min(availableForGas / feeCap, (UInt256)long.MaxValue);
                if (tx.GasLimit > allowance)
                    tx.GasLimit = allowance;
            }

            EstimateGasTracer estimateGasTracer = new();
            TransactionResult tryCallResult = TryCallAndRestore(nonceReader, txProcessor, header, tx, true,
                blobBaseFeeOverride, estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(txProcessor, worldState, specProvider, blocksConfig);

            string? error = tryCallResult.GetErrorMessage(estimateGasTracer.Error);
            string? probeError = error;

            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer, out string? err, errorMargin, cancellationToken);
            error = err switch
            {
                // Probe failed only because gas hint was below standard intrinsic: if estimation succeeds, clear the probe error.
                null when tryCallResult.Error == TransactionResult.ErrorType.GasLimitBelowIntrinsicGas => null,
                null => error,
                _ when error is null => err,
                // Probe's low-gas failure is superseded by whatever the estimator found at full gas.
                _ when tryCallResult.Error == TransactionResult.ErrorType.GasLimitBelowIntrinsicGas => err,
                _ when err.StartsWith(GasEstimator.GasExceedsAllowanceMsgPrefix, StringComparison.Ordinal) => err,
                GasEstimator.InsufficientBalance => err,
                GasEstimator.InsufficientFundsForGas => err,
                _ => error
            };

            bool executionReverted = err is not null
                ? estimateGasTracer.TopLevelRevert // err comes from GasEstimator; TopLevelRevert is authoritative for revert detection here.
                : tryCallResult.EvmExceptionType == EvmExceptionType.Revert;

            return new CallOutput
            {
                Error = error,
                GasSpent = estimate,
                OutputData = estimateGasTracer.ReturnValue,
                InputError = !executionReverted && error is not null && (error != probeError),
                ExecutionReverted = executionReverted
            };
        }

        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, bool optimize, UInt256? blobBaseFeeOverride, CancellationToken cancellationToken) =>
            HasOverrides(stateOverride, blobBaseFeeOverride, blockOverride: null)
                ? CreateAccessListExclusive(header, tx, stateOverride, optimize, blobBaseFeeOverride, cancellationToken)
                : CreateAccessListShareable(header, tx, optimize, cancellationToken);

        private CallOutput CreateAccessListShareable(BlockHeader header, Transaction tx, bool optimize, CancellationToken cancellationToken)
        {
            using IReadOnlyTxProcessingScope scope = shareableTxProcessorSource.Build(header);
            AccessList? originalAccessList = tx.AccessList;
            try
            {
                return ConvergeAccessList(stateReader, scope.TransactionProcessor, blobBaseFeeOverride: null, header, tx, optimize, cancellationToken);
            }
            finally
            {
                tx.AccessList = originalAccessList;
            }
        }

        private CallOutput CreateAccessListExclusive(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, bool optimize, UInt256? blobBaseFeeOverride, CancellationToken cancellationToken)
        {
            using Scope<BlockProcessingComponents> scope = processingEnv.BuildAndOverride(header, stateOverride);
            BlockProcessingComponents components = scope.Component;
            components.RequestState.BlobBaseFeeOverride = blobBaseFeeOverride;

            AccessList? originalAccessList = tx.AccessList;
            try
            {
                return ConvergeAccessList(components.StateReader, components.TransactionProcessor, blobBaseFeeOverride, header, tx, optimize, cancellationToken);
            }
            finally
            {
                tx.AccessList = originalAccessList;
            }
        }

        // Convergence loop: mirrors Geth's AccessList() — run with the current AL, discover touched
        // slots, repeat until the AL stabilizes. Gas and error come from the final (warm) run, so
        // cold-read overcounting is eliminated and OOG due to AL intrinsic cost is surfaced.
        // Starts from the caller-supplied AL so user-provided entries are preserved and counted.
        private CallOutput ConvergeAccessList(IStateReader nonceReader, ITransactionProcessor txProcessor, UInt256? blobBaseFeeOverride, BlockHeader header, Transaction tx, bool optimize, CancellationToken cancellationToken)
        {
            // Loop-invariant: the addresses to filter from the discovered AL depend only on header
            // and tx, neither of which change between iterations. Compute once and reuse.
            FrozenSet<AddressAsKey> precompiles = specProvider.GetSpec(header).Precompiles;
            int bufferSize = (optimize ? 3 : 1) + precompiles.Count;
            Address[] addressBuffer = new Address[bufferSize];
            FillAddressesToOptimize(addressBuffer, header, tx, optimize, precompiles);

            AccessList? previousAccessList = tx.AccessList;
            AccessTxTracer accessTracer = new(addressBuffer);
            CallOutputTracer outputTracer = new();
            CancellationTxTracer tracer = new CompositeTxTracer(outputTracer, accessTracer).WithCancellation(cancellationToken);
            TransactionResult result;
            bool stop;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                accessTracer.Reset();
                outputTracer.Reset();
                tx.AccessList = previousAccessList;
                result = TryCallAndRestore(nonceReader, txProcessor, header, tx, false, blobBaseFeeOverride, tracer);
                stop = !result.TransactionExecuted || HasConverged(previousAccessList, accessTracer.AccessList);
                previousAccessList = accessTracer.AccessList;
            } while (!stop);

            bool executionReverted = result.EvmExceptionType == EvmExceptionType.Revert;
            // Geth always surfaces plain "execution reverted" for eth_createAccessList,
            // regardless of whether the revert payload carries a decoded reason.
            string? error = executionReverted
                ? "execution reverted"
                : result.GetErrorMessage(outputTracer.Error);

            return new CallOutput
            {
                Error = error,
                GasSpent = outputTracer.GasSpent,
                OperationGas = outputTracer.OperationGas,
                OutputData = outputTracer.ReturnValue,
                InputError = !result.TransactionExecuted,
                ExecutionReverted = executionReverted,
                AccessList = accessTracer.AccessList,
            };
        }

        private void FillAddressesToOptimize(Span<Address> buffer, BlockHeader header, Transaction tx, bool optimize, FrozenSet<AddressAsKey> precompiles)
        {
            int idx;
            if (!optimize)
            {
                buffer[0] = header.GasBeneficiary;
                idx = 1;
            }
            else
            {
                // EIP-2930: sender, recipient and gas beneficiary are implicitly accessed,
                // so excluding them keeps the returned access list minimal.
                UInt256 senderNonce = tx.IsContractCreation ? stateReader.GetNonce(header, tx.SenderAddress) : UInt256.Zero;
                buffer[0] = tx.SenderAddress;
                buffer[1] = tx.GetRecipient(senderNonce);
                buffer[2] = header.GasBeneficiary;
                idx = 3;
            }

            foreach (AddressAsKey p in precompiles)
                buffer[idx++] = p.Value;
        }

        private static bool HasConverged(AccessList? previous, AccessList? discovered)
        {
            // Count comparison is sufficient because WarmUp(tx.AccessList) pre-populates the warm-address
            // set with all of `previous`'s entries before execution, making `discovered` monotonically
            // non-decreasing (discovered ⊇ previous). Equal counts therefore imply equal content.
            (int addrs, int keys) previousCount = previous?.Count ?? (0, 0);
            (int addrs, int keys) discoveredCount = discovered?.Count ?? (0, 0);
            return previousCount == discoveredCount;
        }

        private TransactionResult TryCallAndRestore(
            IStateReader nonceReader,
            ITransactionProcessor txProcessor,
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            UInt256? blobBaseFeeOverride,
            ITxTracer tracer)
        {
            try
            {
                return CallAndRestore(nonceReader, txProcessor, blockHeader, transaction, treatBlockHeaderAsParentBlock, blobBaseFeeOverride, tracer);
            }
            catch (InsufficientBalanceException)
            {
                return TransactionResult.InsufficientSenderBalance;
            }
        }

        private TransactionResult CallAndRestore(
            IStateReader nonceReader,
            ITransactionProcessor txProcessor,
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            UInt256? blobBaseFeeOverride,
            ITxTracer tracer)
        {
            transaction.SenderAddress ??= Address.Zero;

            //Ignore nonce on all CallAndRestore calls
            transaction.Nonce = nonceReader.GetNonce(blockHeader, transaction.SenderAddress);

            BlockHeader callHeader = blockHeader.Clone();
            if (treatBlockHeaderAsParentBlock)
            {
                callHeader.Number += 1;
                callHeader.UnclesHash = Keccak.OfAnEmptySequenceRlp;
                callHeader.Beneficiary = Address.Zero;
                callHeader.Difficulty = UInt256.Zero;
                callHeader.Timestamp = Math.Max(blockHeader.Timestamp + blocksConfig.SecondsPerSlot, timestamper.UnixTime.Seconds);
            }

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

                BlobGasCalculator.TryCalculateFeePerBlobGas(callHeader, releaseSpec.BlobBaseFeeUpdateFraction, out blobBaseFee);

                if (blobBaseFeeOverride.HasValue)
                    blobBaseFee = blobBaseFeeOverride.Value;

                if (transaction.Type is TxType.Blob && transaction.MaxFeePerBlobGas is null)
                {
                    transaction.MaxFeePerBlobGas = blobBaseFee;
                }
            }
            callHeader.MixHash = blockHeader.MixHash;
            callHeader.IsPostMerge = blockHeader.Difficulty == 0;
            transaction.Hash = transaction.CalculateHash();
            BlockExecutionContext blockExecutionContext = new(callHeader, releaseSpec, blobBaseFee);
            return txProcessor.CallAndRestore(transaction, in blockExecutionContext, tracer);
        }

        public ulong GetChainId() => blockTree.ChainId;

        public bool FilterExists(int filterId) => filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => filterStore.GetFilterType(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            HashSet<AddressAsKey>? addresses = null,
            IEnumerable<Hash256[]?>? topics = null,
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = GetFilter(fromBlock, toBlock, addresses, topics);
            return logFinder.FindLogs(filter, cancellationToken);
        }

        public LogFilter GetFilter(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            HashSet<AddressAsKey>? addresses = null,
            IEnumerable<Hash256[]?>? topics = null) => filterStore.CreateLogFilter(fromBlock, toBlock, addresses, topics, false);

        public IEnumerable<FilterLog> GetLogs(
            LogFilter filter,
            BlockHeader fromBlock,
            BlockHeader toBlock,
            CancellationToken cancellationToken = default) => logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);

        public bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default)
        {
            LogFilter? filter;
            filterLogs = null;
            if ((filter = filterStore.GetFilter<LogFilter>(filterId)) is not null)
                filterLogs = logFinder.FindLogs(filter, cancellationToken);

            return filter is not null;
        }

        public int NewFilter(BlockParameter fromBlock, BlockParameter toBlock,
            HashSet<AddressAsKey>? address = null, IEnumerable<Hash256[]?>? topics = null)
        {
            LogFilter filter = filterStore.CreateLogFilter(fromBlock, toBlock, address, topics);
            filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = filterStore.CreateBlockFilter();
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

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>
            => stateReader.RunTreeVisitor(treeVisitor, baseBlock, diagnostics: diagnostics);

        public bool HasStateForBlock(BlockHeader? baseBlock) => stateReader.HasStateForBlock(baseBlock);

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default) => logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default) => logFinder.FindLogs(filter, cancellationToken);

        public ReadOnlyBlockAccessList? GetBlockAccessList(long blockNumber, Hash256 blockHash)
            => balStore.Get(blockNumber, blockHash);

        public MemoryManager<byte>? GetBlockAccessListRlp(long blockNumber, Hash256 blockHash)
            => balStore.GetRlp(blockNumber, blockHash);

        // for testing
        public void DeleteBlockAccessList(long blockNumber, Hash256 blockHash)
            => balStore.Delete(blockNumber, blockHash);

        public Witness GenerateExecutionWitness(BlockHeader parent, Block block)
        {
            RecoverTxSenders(block);
            using IWitnessGeneratingBlockProcessingEnvScope scope = witnessGeneratingBlockProcessingEnvFactory.Value.CreateScope();
            IExistingBlockWitnessCollector witnessCollector = scope.Env.CreateExistingBlockWitnessCollector();
            return witnessCollector.GetWitnessForExistingBlock(parent, block);
        }

        public SingleCallWitnessResult GenerateExecutionWitness(BlockHeader header, Transaction tx, CancellationToken cancellationToken = default)
        {
            using IWitnessGeneratingBlockProcessingEnvScope scope = witnessGeneratingBlockProcessingEnvFactory.Value.CreateScope();
            ISingleCallWitnessCollector collector = scope.Env.CreateSingleCallWitnessCollector();
            return collector.ExecuteCallAndCollectWitness(header, tx, cancellationToken);
        }

        public record BlockProcessingComponents(
            IStateReader StateReader,
            ITransactionProcessor TransactionProcessor,
            IWorldState WorldState,
            SingleCallRequestState RequestState
        );
    }

    public interface IBlockchainBridgeFactory
    {
        IBlockchainBridge CreateBlockchainBridge();
    }

    public class BlockchainBridgeFactory(
        ISimulateReadOnlyBlocksProcessingEnvFactory simulateEnvFactory,
        IOverridableEnvFactory envFactory,
        ILifetimeScope rootLifetimeScope,
        int overridableEnvPoolSize
    ) : IBlockchainBridgeFactory
    {
        public IBlockchainBridge CreateBlockchainBridge()
        {
            ShareableOverridableEnvSource<BlockchainBridge.BlockProcessingComponents> blockProcessingEnv = new(
                BuildSingleEnv, overridableEnvPoolSize);

            ILifetimeScope blockchainBridgeLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddScoped<BlockchainBridge>()
                .AddScoped<ISimulateReadOnlyBlocksProcessingEnv>((_) => simulateEnvFactory.Create())
                .AddScoped<IShareableOverridableEnvSource<BlockchainBridge.BlockProcessingComponents>>(blockProcessingEnv));

            blockchainBridgeLifetime.Disposer.AddInstanceForDisposal(blockProcessingEnv);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(blockchainBridgeLifetime);

            return blockchainBridgeLifetime.Resolve<BlockchainBridge>();
        }

        // One env per invocation — independent _worldScopeCloser and decorator chain so concurrent renters share no mutable state.
        private IOverridableEnv<BlockchainBridge.BlockProcessingComponents> BuildSingleEnv()
        {
            IOverridableEnv env = envFactory.Create();
            ILifetimeScope overridableScopeLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddModule(env)
                .AddScoped<SingleCallRequestState>()
                .BindScoped<IBlobBaseFeeOverrideProvider, SingleCallRequestState>()
                .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeOverrideCalculatorDecorator>()
                .Add<BlockchainBridge.BlockProcessingComponents>());

            // Pool owns the scope. Registering with rootLifetimeScope.Disposer would retain every created env until shutdown
            // and turn burst override traffic into long-lived memory.
            IOverridableEnv<BlockchainBridge.BlockProcessingComponents> inner =
                overridableScopeLifetime.Resolve<IOverridableEnv<BlockchainBridge.BlockProcessingComponents>>();
            return new DisposableOverridableEnv(inner, overridableScopeLifetime);
        }

        private sealed class DisposableOverridableEnv(
            IOverridableEnv<BlockchainBridge.BlockProcessingComponents> inner,
            IDisposable scope) : IOverridableEnv<BlockchainBridge.BlockProcessingComponents>, IDisposable
        {
            public Scope<BlockchainBridge.BlockProcessingComponents> BuildAndOverride(
                BlockHeader? header,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                IReleaseSpec? specOverride = null,
                BlockOverride? blockOverride = null) =>
                inner.BuildAndOverride(header, stateOverride, specOverride, blockOverride);

            public void Dispose() => scope.Dispose();
        }
    }
}
