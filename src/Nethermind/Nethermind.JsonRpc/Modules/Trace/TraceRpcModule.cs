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
            return BuildStreamingReplayResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    using StreamingParityLikeBlockTracer tracer = new(
                        traceTypeByTransaction,
                        defaultTypes: ParityTraceTypes.None,
                        ParityTraceStreamMode.Replay,
                        includeTxHash: false,
                        writer, pipeWriter, token);
                    TraceBlockWithCancellation(block, tracer, token);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> traces = TraceBlock(block, new ParityLikeBlockTracer(traceTypeByTransaction));
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
                Rlp.ValueDecoderContext ctx = data.AsRlpValueContext();
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

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            // trace_call / trace_rawTransaction stay buffered: deferring execution past the
            // RPC call boundary loses state-override mutations (e.g. balance overrides) — the
            // worldstate scope is shared with block-processor setup that runs between the
            // scope open and the deferred Trace call. Single-tx working sets are small so
            // the streaming win is marginal here anyway.
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
            ITracer tracer = env.Component;

            ParityLikeBlockTracer parityTracer = new(traceTypes1);
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            tracer.Trace(block, parityTracer.WithCancellation(timeout.Token));
            IReadOnlyCollection<ParityLikeTxTrace> result = parityTracer.BuildResult();
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
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
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;
            ParityTraceTypes parityTypes = GetParityTypes(traceTypes);
            return BuildStreamingReplaySingleResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    using StreamingParityLikeBlockTracer tracer = new(
                        txHash,
                        parityTypes,
                        ParityTraceStreamMode.Replay,
                        includeTxHash: false,
                        writer, pipeWriter, token);
                    ExecuteBlockWithCancellation(parentHeader, block, tracer, specOverride: null, token);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(txHash, parityTypes));
                    return new ParityTxTraceFromReplay(txTrace);
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
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(parentSearch.Object);
            }

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            BlockHeader parentHeader = parentSearch.Object!;
            return BuildStreamingReplayResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    using StreamingParityLikeBlockTracer tracer = new(
                        traceTypes1,
                        ParityTraceStreamMode.Replay,
                        includeTxHash: true,
                        writer, pipeWriter, token);
                    ExecuteBlockWithCancellation(parentHeader, block, tracer, specOverride: null, token);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> traces = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(traceTypes1));
                    return traces.Select(static t => new ParityTxTraceFromReplay(t, true));
                });
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            BlockParameter fromBlock = traceFilterForRpc.FromBlock ?? BlockParameter.Latest;
            BlockParameter toBlock = traceFilterForRpc.ToBlock ?? BlockParameter.Latest;

            // Probe positions 0-1 so range/header errors surface as RPC errors; once
            // streaming starts the only signal we have for later block failures is silent
            // truncation (logged below).
            using (IEnumerator<SearchResult<Block>> probe = blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock).GetEnumerator())
            {
                for (int i = 0; i < 2 && probe.MoveNext(); i++)
                {
                    if (probe.Current.IsError)
                    {
                        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(probe.Current);
                    }
                }
            }

            return BuildStreamingStoreResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    // Per-item filter applied during emission — the After/Count counters
                    // advance inline, so we never accumulate items.
                    TxTraceFilter streamingFilter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);
                    StreamingParityLikeBlockTracer.StoreItemPredicate predicate = streamingFilter.ShouldUseTxTrace;

                    IEnumerable<SearchResult<Block>> blocksSearch =
                        blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock);

                    foreach (SearchResult<Block> blockSearch in blocksSearch)
                    {
                        if (streamingFilter.IsExhausted) break;
                        if (blockSearch.IsError)
                        {
                            if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: block lookup failed mid-stream ({blockSearch.Error}).");
                            break;
                        }

                        Block block = blockSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(block.Header))
                        {
                            if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: missing state for block {block.Number}.");
                            break;
                        }

                        SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                        if (parentSearch.IsError)
                        {
                            if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: parent lookup failed for block {block.Number} ({parentSearch.Error}).");
                            break;
                        }
                        BlockHeader parentHeader = parentSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(parentHeader))
                        {
                            if (_logger.IsWarn) _logger.Warn($"trace_filter stream truncated: missing state for parent of block {block.Number}.");
                            break;
                        }

                        using StreamingParityLikeBlockTracer tracer = new(
                            ParityTraceTypes.Trace | ParityTraceTypes.Rewards,
                            ParityTraceStreamMode.Store,
                            includeTxHash: false,
                            writer, pipeWriter, token,
                            storeFilter: predicate);
                        ExecuteBlockWithCancellation(parentHeader, block, tracer, specOverride: null, token);
                    }
                },
                runBuffered: () =>
                {
                    // In-process iteration uses the original buffered semantics.
                    List<ParityLikeTxTrace> txTraces = [];
                    foreach (SearchResult<Block> blockSearch in blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock))
                    {
                        if (blockSearch.IsError) break;
                        Block block = blockSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(block.Header)) break;
                        SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                        if (parentSearch.IsError) break;
                        BlockHeader parentHeader = parentSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(parentHeader)) break;

                        IReadOnlyCollection<ParityLikeTxTrace> blockTraces = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));
                        txTraces.AddRange(blockTraces);
                    }
                    TxTraceFilter bufferedFilter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);
                    return bufferedFilter.FilterTxTraces(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
                });
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
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
            }

            if (!TryResolveForkSpec(fork, out IReleaseSpec? forkSpec, out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? forkError))
                return forkError!;

            BlockHeader parentHeader = parentSearch.Object!;
            return BuildStreamingStoreResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    using StreamingParityLikeBlockTracer tracer = new(
                        ParityTraceTypes.Trace | ParityTraceTypes.Rewards,
                        ParityTraceStreamMode.Store,
                        includeTxHash: false,
                        writer, pipeWriter, token);
                    ExecuteBlockWithCancellation(parentHeader, block, tracer, forkSpec, token);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> traces = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.Rewards), forkSpec);
                    return traces.SelectMany(ParityTxTraceFromStore.FromTxTrace);
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
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;
            return BuildStreamingStoreResult(
                runStreaming: (writer, pipeWriter, token) =>
                {
                    using StreamingParityLikeBlockTracer tracer = new(
                        txHash,
                        ParityTraceTypes.Trace,
                        ParityTraceStreamMode.Store,
                        includeTxHash: false,
                        writer, pipeWriter, token);
                    ExecuteBlockWithCancellation(parentHeader, block, tracer, specOverride: null, token);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new(txHash, ParityTraceTypes.Trace));
                    return ParityTxTraceFromStore.FromTxTrace(txTrace);
                });
        }

        /// <summary>
        /// Trace path used by streaming callers: the caller owns the cancellation token
        /// (so a single timeout CTS can span the whole response).
        /// </summary>
        private void TraceBlockWithCancellation(Block block, ParityLikeBlockTracer tracer, CancellationToken cancellationToken)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(block.Header);
            env.Component.Trace(block, tracer.WithCancellation(cancellationToken));
        }

        /// <summary>Buffered equivalent of <see cref="TraceBlockWithCancellation"/> used by the in-process fallback path of streaming results.</summary>
        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            TraceBlockWithCancellation(block, tracer, timeout.Token);
            return tracer.BuildResult();
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer, IReleaseSpec? specOverride = null)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            ExecuteBlockWithCancellation(baseBlock, block, tracer, specOverride, timeout.Token);
            return tracer.BuildResult();
        }

        /// <summary>
        /// Execute path used by streaming callers: the caller owns the cancellation token
        /// (so a single timeout CTS can span the whole response) and consumes results out
        /// of the tracer directly instead of via <c>BuildResult</c>.
        /// </summary>
        private void ExecuteBlockWithCancellation(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer, IReleaseSpec? specOverride, CancellationToken cancellationToken)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                BlockHeader adjustedHeader = AdjustHeaderForSpec(block.Header, baseBlock, specOverride);
                blockToExecute = block.WithReplacedHeader(adjustedHeader);
            }

            using Scope<ITracer> env = tracerEnv.BuildAndOverride(baseBlock, specOverride: specOverride);
            env.Component.Execute(blockToExecute, tracer.WithCancellation(cancellationToken));
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

        /// <summary>
        /// Wraps an execution delegate as a streaming Replay-mode response. Ownership of
        /// the timeout CTS passes to the returned result. <paramref name="runBuffered"/>
        /// is the in-process iteration fallback.
        /// </summary>
        private ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> BuildStreamingReplayResult(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<IEnumerable<ParityTxTraceFromReplay>> runBuffered)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(runBuffered());
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(
                    new ParityTxTraceStreamingResult<ParityTxTraceFromReplay>(runStreaming, timeoutCts, _logger)
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

        /// <summary><inheritdoc cref="BuildStreamingReplayResult"/></summary>
        private ResultWrapper<IEnumerable<ParityTxTraceFromStore>> BuildStreamingStoreResult(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<IEnumerable<ParityTxTraceFromStore>> runBuffered)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(runBuffered());
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(
                    new ParityTxTraceStreamingResult<ParityTxTraceFromStore>(runStreaming, timeoutCts, _logger)
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

        /// <summary>
        /// Wraps an execution delegate as a streaming single-envelope Replay response used
        /// by <c>trace_call</c> / <c>trace_rawTransaction</c> / <c>trace_replayTransaction</c>.
        /// <paramref name="lifetimeScope"/> (if supplied) is owned by the returned result and
        /// disposed once the response is consumed — used to extend the lifetime of an
        /// eagerly-opened state-override scope across the deferred trace execution.
        /// </summary>
        private ResultWrapper<ParityTxTraceFromReplay> BuildStreamingReplaySingleResult(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<ParityTxTraceFromReplay> runBuffered,
            IDisposable? lifetimeScope = null)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                try
                {
                    return ResultWrapper<ParityTxTraceFromReplay>.Success(runBuffered());
                }
                finally
                {
                    lifetimeScope?.Dispose();
                }
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Success(
                    new ParityTxTraceFromReplayStreamingResult(runStreaming, timeoutCts, _logger)
                    {
                        MaterializeForInProcess = runBuffered,
                        LifetimeScope = lifetimeScope,
                    });
            }
            catch
            {
                lifetimeScope?.Dispose();
                timeoutCts.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) => new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new ParityStyleSimulateBlockTracerFactory(types: GetParityTypes(traceTypes ?? ["Trace"])), _secondsPerSlot)
                .Execute(payload, blockParameter);
    }
}
