// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Nethermind.Evm.State;
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
        IPrefixStateSeedSource prefixSeeds,
        ILogManager logManager,
        IParallelBlockTracer? parallelTracer = null)
        : ITraceRpcModule
    {
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly ILogger _logger = logManager.GetClassLogger<TraceRpcModule>();

        /// <summary>
        /// Parses Parity trace type names (case-insensitive) into a combined <see cref="ParityTraceTypes"/> flag set.
        /// </summary>
        /// <returns><see langword="false"/> when <paramref name="types"/> is <see langword="null"/> or contains an unknown name.</returns>
        public static bool TryGetParityTypes(string[]? types, out ParityTraceTypes result)
        {
            result = ParityTraceTypes.None;
            // A JSON null per-call selection in trace_callMany reaches here.
            if (types is null) return false;
            foreach (string type in types)
            {
                if (!FastEnum.TryParse(type, ignoreCase: true, out ParityTraceTypes parsed))
                {
                    result = ParityTraceTypes.None;
                    return false;
                }
                result |= parsed;
            }
            return true;
        }

        public static ResultWrapper<T> InvalidTraceTypes<T>() => ResultWrapper<T>.Fail("Invalid trace types", ErrorCodes.InvalidParams);

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
                if (!TryGetParityTypes(calls[i].TraceTypes, out ParityTraceTypes traceTypes))
                {
                    return InvalidTraceTypes<IEnumerable<ParityTxTraceFromReplay>>();
                }

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

            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes))
            {
                return InvalidTraceTypes<ParityTxTraceFromReplay>();
            }

            BlockHeader header = headerSearch.Object!.Clone();
            Block block = new(header, [tx], []);

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
                    return new ParityTxTraceFromReplay(result.SingleOrDefault());
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
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay>(parentSearch.Object);
            }

            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes))
            {
                return InvalidTraceTypes<ParityTxTraceFromReplay>();
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, parityTypes, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, transactionHash: txHash);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(txHash, parityTypes), transactionHash: txHash);
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
            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes traceTypes1))
            {
                return InvalidTraceTypes<IEnumerable<ParityTxTraceFromReplay>>();
            }

            // Genesis has no parent to replay from, and no transactions or rewards to trace.
            if (block.IsGenesis)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success([]);
            }

            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingMultiResult<ParityTxTraceFromReplay>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        traceTypes1, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct);
                    if (!TryStreamBlockInParallel(parentHeader, block, traceTypes1, streamingTracer, ct))
                        ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlockParallelOrReplay(parentHeader, block, traceTypes1);
                    return txTraces.Select(static t => new ParityTxTraceFromReplay(t, true));
                });
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            bool ownsTimeout = true;
            try
            {
                BlockParameter fromBlock = traceFilterForRpc.FromBlock ?? BlockParameter.Latest;
                BlockParameter toBlock = traceFilterForRpc.ToBlock ?? BlockParameter.Latest;

                // Collect the whole range first so search errors (e.g. from > to) take precedence over state checks.
                List<(Block Block, BlockHeader? Parent)> blocks = [];
                foreach (SearchResult<Block> blockSearch in blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (blockSearch.IsError)
                    {
                        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
                    }
                    Block block = blockSearch.Object!;
                    if (!block.IsGenesis) blocks.Add((block, null));
                }

                BlockHeader? previous = null;
                for (int i = 0; i < blocks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Block block = blocks[i].Block;
                    if (!blockchainBridge.HasStateForBlock(block.Header))
                    {
                        return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    BlockHeader parentHeader;
                    if (previous is not null && previous.Hash == block.Header.ParentHash)
                    {
                        parentHeader = previous;
                    }
                    else
                    {
                        SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                        if (parentSearch.IsError)
                        {
                            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
                        }
                        parentHeader = parentSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(parentHeader))
                        {
                            return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentHeader);
                        }
                    }

                    blocks[i] = (block, parentHeader);
                    previous = block.Header;
                }

                ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
                TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);

                cancellationToken.ThrowIfCancellationRequested();
                ownsTimeout = false;
                return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                    runStreaming: (writer, pipeWriter, ct) =>
                    {
                        using StreamingParityLikeBlockTracer streamingTracer = new(
                            types, ParityTraceStreamMode.Store, includeTxHash: false,
                            writer, pipeWriter, ct, storeFilter: filter);
                        foreach ((Block block, BlockHeader? parentHeader) in blocks)
                        {
                            if (!TryStreamBlockInParallel(parentHeader!, block, types, streamingTracer, ct))
                                ExecuteBlockStreaming(parentHeader!, block, streamingTracer, ct);
                        }
                    },
                    runBuffered: () => RunBufferedTraceFilter(blocks, filter, cancellationToken),
                    timeoutCts: timeout);
            }
            finally
            {
                if (ownsTimeout) timeout.Dispose();
            }
        }

        private IEnumerable<ParityTxTraceFromStore> RunBufferedTraceFilter(List<(Block Block, BlockHeader? Parent)> blocks, TxTraceFilter filter, CancellationToken cancellationToken)
        {
            List<ParityLikeTxTrace> txTraces = [];
            foreach ((Block block, BlockHeader? parentHeader) in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                txTraces.AddRange(ExecuteBlockParallelOrReplay(parentHeader!, block, ParityTraceTypes.Trace | ParityTraceTypes.Rewards, cancellationToken));
            }
            return filter.FilterTxTraces(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
        {
            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!TryResolveForkSpec(fork, out IReleaseSpec? forkSpec, out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? forkError))
                return forkError!;

            // Genesis has no parent to replay from, and no transactions or rewards to trace.
            if (block.IsGenesis)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success([]);
            }

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

            BlockHeader parentHeader = parentSearch.Object!;
            ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        types, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct);
                    if (forkSpec is not null || !TryStreamBlockInParallel(parentHeader, block, types, streamingTracer, ct))
                        ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, forkSpec);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = forkSpec is null
                        ? ExecuteBlockParallelOrReplay(parentHeader, block, types)
                        : ExecuteBlock(parentHeader, block, new(types), forkSpec);
                    return txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);
                });
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Hash256 txHash, long[] positions)
        {
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction = trace_transaction(txHash);
            if (!traceTransaction.Result) return traceTransaction;
            using (traceTransaction)
            {
                List<ParityTxTraceFromStore> traces = ExtractPositionsFromTxTrace(positions, traceTransaction);
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces);
            }
        }

        public static List<ParityTxTraceFromStore> ExtractPositionsFromTxTrace(long[] positions, ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction)
        {
            List<ParityTxTraceFromStore> traces = [];
            ParityTxTraceFromStore[] transactionTraces = traceTransaction.Data.ToArray();
            for (int index = 0; index < positions.Length; index++)
            {
                long position = positions[index];
                if (position >= -1 && position < transactionTraces.Length - 1)
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

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, transactionHash: txHash);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new(txHash, ParityTraceTypes.Trace), transactionHash: txHash);
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

        /// <summary>A covered block is traced one transaction per worker, rewards last on the seeded end state; any
        /// other block is executed as before. Both produce the traces a full execution produces.</summary>
        private bool TryExecuteBlockInParallel(BlockHeader parent, Block block, ParityTraceTypes types, CancellationToken cancellationToken, [NotNullWhen(true)] out IReadOnlyList<ParityLikeTxTrace>? traces)
        {
            traces = null;
            if (parallelTracer is null) return false;

            return parallelTracer.TryTrace(block, parent,
                PerTransaction(types), Rewards(types), cancellationToken, out traces);
        }

        /// <summary>The same, writing each transaction's traces out as the workers finish them, so the caller reads
        /// the first of them while the block is still being traced and the block is never held whole.</summary>
        private bool TryStreamBlockInParallel(BlockHeader parent, Block block, ParityTraceTypes types, StreamingParityLikeBlockTracer streamingTracer, CancellationToken cancellationToken) =>
            parallelTracer is not null && parallelTracer.TryStream(block, parent,
                PerTransaction(types), Rewards(types), streamingTracer.WriteTraces, cancellationToken);

        private static Func<IWorldState, Hash256, IBlockTracer<ParityLikeTxTrace>> PerTransaction(ParityTraceTypes types)
        {
            ParityTraceTypes perTransaction = types & ~ParityTraceTypes.Rewards;
            return (_, txHash) => new ParityLikeBlockTracer(txHash, perTransaction);
        }

        private static Func<IWorldState, IBlockTracer<ParityLikeTxTrace>>? Rewards(ParityTraceTypes types) =>
            (types & ParityTraceTypes.Rewards) == ParityTraceTypes.Rewards ? _ => new ParityLikeBlockTracer(types) : null;

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlockParallelOrReplay(BlockHeader parent, Block block, ParityTraceTypes types, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource? timeout = cancellationToken.CanBeCanceled ? null : BuildTimeoutCancellationTokenSource();
            CancellationToken token = timeout?.Token ?? cancellationToken;
            token.ThrowIfCancellationRequested();
            return TryExecuteBlockInParallel(parent, block, types, token, out IReadOnlyList<ParityLikeTxTrace>? traces)
                ? traces
                : ExecuteBlock(parent, block, new ParityLikeBlockTracer(types), cancellationToken: token);
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer, IReleaseSpec? specOverride = null, Hash256? transactionHash = null, CancellationToken cancellationToken = default)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                BlockHeader adjustedHeader = AdjustHeaderForSpec(block.Header, baseBlock, specOverride);
                blockToExecute = block.WithReplacedHeader(adjustedHeader);
            }

            using Scope<ITracer> env = tracerEnv.BuildAndOverrideAtTarget(blockToExecute.Header, specOverride: specOverride);
            ITracer tracer2 = env.Component;

            using CancellationTokenSource? timeout = cancellationToken.CanBeCanceled ? null : BuildTimeoutCancellationTokenSource();
            CancellationToken token = timeout?.Token ?? cancellationToken;
            token.ThrowIfCancellationRequested();
            // A prefix recorded under the block's own spec says nothing about the block run under another one, even
            // though the header keeps its hash: no seed when the spec is overridden.
            tracer2.Execute(blockToExecute, TransactionTraceBoundary.Wrap(tracer.WithCancellation(token), transactionHash, specOverride is null ? prefixSeeds : null));
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
            Func<IEnumerable<T>> runBuffered,
            CancellationTokenSource? timeoutCts = null)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                using (timeoutCts)
                {
                    return ResultWrapper<IEnumerable<T>>.Success(runBuffered());
                }
            }

            timeoutCts ??= BuildTimeoutCancellationTokenSource();
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

        private void ExecuteBlockStreaming(BlockHeader baseHeader, Block block, ParityLikeBlockTracer tracer, CancellationToken ct, IReleaseSpec? specOverride = null, Hash256? transactionHash = null)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                blockToExecute = block.WithReplacedHeader(AdjustHeaderForSpec(block.Header, baseHeader, specOverride));
            }
            using Scope<ITracer> env = tracerEnv.BuildAndOverrideAtTarget(blockToExecute.Header, specOverride: specOverride);
            env.Component.Execute(blockToExecute, TransactionTraceBoundary.Wrap(tracer.WithCancellation(ct), transactionHash, specOverride is null ? prefixSeeds : null));
        }

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) =>
            TryGetParityTypes(traceTypes ?? ["Trace"], out ParityTraceTypes parityTypes)
                ? new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new ParityStyleSimulateBlockTracerFactory(types: parityTypes), _secondsPerSlot)
                    .Execute(payload, blockParameter)
                : InvalidTraceTypes<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>>();
    }
}
