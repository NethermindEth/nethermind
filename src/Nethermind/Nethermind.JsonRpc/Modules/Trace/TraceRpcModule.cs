// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Trace
{
    /// <summary>
    /// All methods that receive transaction from users uses ITransactionProcessor.Trace
    /// As user might send transaction without gas and/or sender we can't charge gas fees here
    /// So at the end stateDiff will be a bit incorrect
    ///
    /// All methods that traces transactions from chain uses ITransactionProcessor.Execute
    /// From-chain transactions should have stateDiff as we got during normal execution. Also we are sure that sender have enough funds to pay gas
    /// </summary>
    public class TraceRpcModule(
        IReceiptFinder receiptFinder,
        IOverridableEnv<ITracer> tracerEnv,
        IBlockFinder blockFinder,
        IJsonRpcConfig jsonRpcConfig,
        IBlockchainBridge blockchainBridge,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
        : ITraceRpcModule
    {
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly ILogger _logger = logManager.GetClassLogger<TraceRpcModule>();

        public static ParityTraceTypes GetParityTypes(string[] types) =>
            types.Select(static s => FastEnum.Parse<ParityTraceTypes>(s, true)).Aggregate(static (t1, t2) => t1 | t2);

        /// <summary>
        /// Traces one transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null)
        {
            blockParameter ??= BlockParameter.Latest;

            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);

            Result<Transaction> txResult = call.ToTransaction(validateUserInput: true, gasCap: jsonRpcConfig.GasCap, spec: specProvider.GetSpec(headerSearch.Object!));
            return !txResult.Success(out Transaction? transaction, out string? error)
                ? ResultWrapper<ParityTxTraceFromReplay>.Fail(error, ErrorCodes.InvalidInput)
                : TraceTx(transaction, traceTypes, blockParameter, stateOverride);
        }

        /// <summary>
        /// Traces list of transactions. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TraceCallManyRequest request, BlockParameter? blockParameter = null)
        {
            using TraceCallManyRequest _ = request;
            ArrayPoolList<TransactionForRpcWithTraceTypes> calls = request.Calls;
            blockParameter ??= BlockParameter.Latest;

            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!;
            if (!blockchainBridge.HasStateForBlock(header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(header);
            }

            Dictionary<Hash256, ParityTraceTypes> traceTypeByTransaction = new(calls.Count);
            Transaction[] txs = new Transaction[calls.Count];
            for (int i = 0; i < calls.Count; i++)
            {
                Result<Transaction> txResult = calls[i].Transaction.ToTransaction(validateUserInput: true, gasCap: jsonRpcConfig.GasCap, spec: specProvider.GetSpec(header));
                if (!txResult.Success(out Transaction? tx, out string? error))
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(error, ErrorCodes.InvalidInput);
                }

                tx.Hash = new Hash256(new UInt256((ulong)i).ToValueHash());
                ParityTraceTypes traceTypes = GetParityTypes(calls[i].TraceTypes);
                txs[i] = tx;
                traceTypeByTransaction.Add(tx.Hash, traceTypes);
            }

            Block block = new(header, new BlockBody(txs, []));

            return BuildStreamingMultiResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        traceTypeByTransaction, ParityTraceTypes.None,
                        ParityTraceStreamMode.Replay, includeTxHash: false,
                        writer, pipeWriter, ct);
                    TraceBlockStreaming(block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> traces = TraceBlock(block, new(traceTypeByTransaction));
                    return traces.Select(static t => new ParityTxTraceFromReplay(t));
                });
        }

        /// <summary>
        /// Traces one raw transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            try
            {
                RlpReader ctx = new(data);
                Transaction tx = _txDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
                tx.CapGasLimit(jsonRpcConfig.GasCap);
                return TraceTx(tx, traceTypes, BlockParameter.Latest);
            }
            catch (RlpException)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
            }
        }

        private ResultWrapper<ParityTxTraceFromReplay> TraceTx(Transaction tx, string[] traceTypes, BlockParameter blockParameter,
            Dictionary<Address, AccountOverride>? stateOverride = null)
        {
            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!.Clone();
            Block block = new(header, [tx], []);
            ParityTraceTypes parityTypes = GetParityTypes(traceTypes);

            return BuildStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        parityTypes, ParityTraceStreamMode.Replay, includeTxHash: false,
                        writer, pipeWriter, ct);
                    using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
                    env.Component.Trace(block, streamingTracer.WithCancellation(ct));
                },
                runBuffered: () =>
                {
                    using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
                    IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlockDirect(env.Component, block, new(parityTypes));
                    return new ParityTxTraceFromReplay(result.Single());
                });
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Hash256 txHash, string[] traceTypes, bool traceNonCanonical = false)
        {
            SearchResult<Hash256> blockHashSearch = receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!, requireCanonical: !traceNonCanonical));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash!));
            if (parentSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object!))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay>(parentSearch.Object!);
            }

            BlockHeader parentHeader = parentSearch.Object!;
            ParityTraceTypes parityTypes = GetParityTypes(traceTypes);

            return BuildStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, parityTypes, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(txHash, parityTypes));
                    return new ParityTxTraceFromReplay(txTrace, includeTransactionHash: true);
                });
        }

        /// <summary>
        /// Traces one block. As it replays existing block will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
        {
            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash!));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object!))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(parentSearch.Object!);
            }

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingMultiResult<ParityTxTraceFromReplay>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        traceTypes1, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(parentHeader, block, new(traceTypes1));
                    return txTraces.Select(static t => new ParityTxTraceFromReplay(t, true));
                });
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            BlockParameter fromBlock = traceFilterForRpc.FromBlock ?? BlockParameter.Latest;
            BlockParameter toBlock = traceFilterForRpc.ToBlock ?? BlockParameter.Latest;

            List<Block> blocks = [];
            foreach (SearchResult<Block> blockSearch in blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock))
            {
                if (blockSearch.IsError)
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
                }
                blocks.Add(blockSearch.Object!);
            }

            ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
            TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        types, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct, storeFilter: filter);
                    foreach (Block block in blocks)
                    {
                        if (!TryResolveParentForTracing(block, out BlockHeader? parentHeader)) break;
                        ExecuteBlockStreaming(parentHeader!, block, streamingTracer, ct);
                    }
                },
                runBuffered: () => RunBufferedTraceFilter(blocks, filter));
        }

        private IEnumerable<ParityTxTraceFromStore> RunBufferedTraceFilter(List<Block> blocks, TxTraceFilter filter)
        {
            List<ParityLikeTxTrace> txTraces = [];
            foreach (Block block in blocks)
            {
                if (!TryResolveParentForTracing(block, out BlockHeader? parentHeader)) break;
                txTraces.AddRange(ExecuteBlock(parentHeader!, block, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards)));
            }
            return filter.FilterTxTraces(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
        }

        private bool TryResolveParentForTracing(Block block, out BlockHeader? parentHeader)
        {
            parentHeader = null;

            if (!blockchainBridge.HasStateForBlock(block.Header))
            {
                if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: missing state for block {block.Header.Number}");
                return false;
            }

            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash!));
            if (parentSearch.IsError)
            {
                if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: parent lookup error for block {block.Header.Number}");
                return false;
            }

            parentHeader = parentSearch.Object!;
            if (!blockchainBridge.HasStateForBlock(parentHeader))
            {
                if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: missing state for parent of block {block.Header.Number}");
                return false;
            }

            return true;
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
        {
            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
            }
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash!));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object!))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object!);
            }

            if (!TryResolveForkSpec(fork, out IReleaseSpec? forkSpec, out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? forkError))
                return forkError!;

            BlockHeader parentHeader = parentSearch.Object!;
            ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        types, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, forkSpec);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(parentHeader, block, new(types), forkSpec);
                    return txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);
                });
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Hash256 txHash, long[] positions)
        {
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction = trace_transaction(txHash);
            List<ParityTxTraceFromStore> traces = ExtractPositionsFromTxTrace(positions, traceTransaction);
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces);
        }

        public static List<ParityTxTraceFromStore> ExtractPositionsFromTxTrace(long[] positions, ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction)
        {
            List<ParityTxTraceFromStore> traces = [];
            ParityTxTraceFromStore[] transactionTraces = traceTransaction.Data.ToArray();
            for (int index = 0; index < positions.Length; index++)
            {
                long position = positions[index];
                if (transactionTraces.Length > position + 1)
                {
                    ParityTxTraceFromStore tr = transactionTraces[position + 1];
                    traces.Add(tr);
                }
            }

            return traces;
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Hash256 txHash, bool traceNonCanonical = false)
        {
            SearchResult<Hash256> blockHashSearch = receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!, requireCanonical: !traceNonCanonical));
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash!));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object!))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object!);
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new(txHash, ParityTraceTypes.Trace));
                    return ParityTxTraceFromStore.FromTxTrace(txTrace);
                });
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(block.Header);
            ITracer tracer2 = env.Component;

            return TraceBlockDirect(tracer2, block, tracer);
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlockDirect(ITracer tracer, Block block, ParityLikeBlockTracer parityTracer)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            tracer.Trace(block, parityTracer.WithCancellation(cancellationToken));
            return parityTracer.BuildResult();
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer, IReleaseSpec? specOverride = null)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                BlockHeader adjustedHeader = AdjustHeaderForSpec(block.Header, baseBlock, specOverride);
                blockToExecute = block.WithReplacedHeader(adjustedHeader);
            }

            using Scope<ITracer> env = tracerEnv.BuildAndOverride(baseBlock, specOverride: specOverride);
            ITracer tracer2 = env.Component;

            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            tracer2.Execute(blockToExecute, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        /// <summary>
        /// Adjusts a block header to the minimum needed for execution under a different spec:
        /// fills in <see cref="BlockHeader.BaseFeePerGas"/> when activating EIP-1559 and
        /// <see cref="BlockHeader.ExcessBlobGas"/> when activating EIP-4844.
        /// </summary>
        /// <remarks>
        /// Known limitation: <c>WithdrawalsRoot</c> (EIP-4895), <c>ParentBeaconBlockRoot</c>
        /// (EIP-4788), and <c>RequestsHash</c> (EIP-7685) are not synthesized. When a block is
        /// re-executed under a spec that activates those features for the first time, system
        /// calls (e.g. the beacon-root contract update) will see <c>null</c>/zero inputs and
        /// produce side-effects that don't match a real chain — acceptable for tracing
        /// (NoValidation path) but trace consumers should be aware. The intended use is
        /// pre-merge fork comparison (e.g. Istanbul ↔ Berlin precompile gas), where these
        /// fields are inherently absent.
        /// </remarks>
        private static BlockHeader AdjustHeaderForSpec(BlockHeader header, BlockHeader parentHeader, IReleaseSpec spec)
        {
            BlockHeader adjusted = header.Clone();

            adjusted.BaseFeePerGas = spec.IsEip1559Enabled
                ? adjusted.BaseFeePerGas.IsZero
                    ? BaseFeeCalculator.Calculate(parentHeader, spec)
                    : adjusted.BaseFeePerGas
                : UInt256.Zero;

            adjusted.ExcessBlobGas = spec.IsEip4844Enabled
                ? BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec)
                : null;

            return adjusted;
        }

        private bool TryResolveForkSpec<TResult>(string? fork, out IReleaseSpec? forkSpec, out ResultWrapper<TResult>? error)
        {
            forkSpec = null;
            error = null;

            if (fork is null)
                return true;

            if (specProvider is not IForkAwareSpecProvider forkAwareProvider)
            {
                error = ResultWrapper<TResult>.Fail("Spec provider does not support fork overrides", ErrorCodes.InvalidParams);
                return false;
            }

            if (string.IsNullOrEmpty(fork))
            {
                error = ResultWrapper<TResult>.Fail("Fork name must not be null or empty", ErrorCodes.InvalidParams);
                return false;
            }

            if (!forkAwareProvider.TryGetForkSpec(fork, out forkSpec))
            {
                error = ResultWrapper<TResult>.Fail($"Unknown fork: '{fork}'. Available: {string.Join(", ", forkAwareProvider.AvailableForks)}", ErrorCodes.InvalidParams);
                return false;
            }

            return true;
        }

        private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
            ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

        private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
            jsonRpcConfig.BuildTimeoutCancellationToken();

        private ResultWrapper<IEnumerable<T>> BuildStreamingMultiResult<T>(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<IEnumerable<T>> runBuffered)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                return ResultWrapper<IEnumerable<T>>.Success(runBuffered());
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<IEnumerable<T>>.Success(new ParityTxTraceStreamingResult<T>(runStreaming, timeoutCts, _logger)
                {
                    MaterializeForInProcess = runBuffered,
                });
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }
        }

        private ResultWrapper<ParityTxTraceFromReplay> BuildStreamingSingleResult(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<ParityTxTraceFromReplay> runBuffered)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Success(runBuffered());
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplayStreamingResult(runStreaming, timeoutCts, _logger)
                {
                    MaterializeForInProcess = runBuffered,
                });
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }
        }

        private void TraceBlockStreaming(Block block, ParityLikeBlockTracer tracer, CancellationToken ct)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(block.Header);
            env.Component.Trace(block, tracer.WithCancellation(ct));
        }

        private void ExecuteBlockStreaming(BlockHeader baseHeader, Block block, ParityLikeBlockTracer tracer, CancellationToken ct, IReleaseSpec? specOverride = null)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                blockToExecute = block.WithReplacedHeader(AdjustHeaderForSpec(block.Header, baseHeader, specOverride));
            }
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(baseHeader, specOverride: specOverride);
            env.Component.Execute(blockToExecute, tracer.WithCancellation(ct));
        }

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) => new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new ParityStyleSimulateBlockTracerFactory(types: GetParityTypes(traceTypes ?? ["Trace"])), _secondsPerSlot)
                .Execute(payload, blockParameter);
    }
}
