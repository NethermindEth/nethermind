// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
        IBlocksConfig blocksConfig)
        : ITraceRpcModule
    {
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;

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
            IAsyncEnumerable<ParityLikeTxTrace> traces = StreamBlockViaChannelAsync(
                block.Header, block,
                (writer, ct) => new ChannelParityLikeBlockTracer(traceTypeByTransaction, writer, ct),
                static (tracer, b, blockTracer) => tracer.Trace(b, blockTracer));
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(
                new ParityTxTraceFromReplayStreamingResult(ToReplayTracesAsync(traces)));
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
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
            ITracer tracer = env.Component;

            IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlockDirect(tracer, block, new(traceTypes1));
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

            IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentSearch.Object, block, new ParityLikeBlockTracer(txHash, GetParityTypes(traceTypes)));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(txTrace));
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
            IAsyncEnumerable<ParityLikeTxTrace> txTraces = StreamBlockViaChannelAsync(
                parentSearch.Object, block,
                (writer, ct) => new ChannelParityLikeBlockTracer(traceTypes1, writer, ct),
                static (tracer, b, blockTracer) => tracer.Execute(b, blockTracer));
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(
                new ParityTxTraceFromReplayStreamingResult(ToReplayTracesAsync(txTraces, includeTransactionHash: true)));
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            SearchResult<BlockHeader> fromSearch = blockFinder.SearchForHeader(traceFilterForRpc.FromBlock ?? BlockParameter.Latest);
            if (fromSearch.IsError)
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(fromSearch);

            SearchResult<BlockHeader> toSearch = blockFinder.SearchForHeader(traceFilterForRpc.ToBlock ?? BlockParameter.Latest);
            if (toSearch.IsError)
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(toSearch);

            if (fromSearch.Object!.Number > toSearch.Object!.Number)
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(
                    $"From block number: {fromSearch.Object.Number} is greater than to block number {toSearch.Object.Number}",
                    ErrorCodes.InvalidInput);

            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(
                new ParityTxTraceFromStoreStreamingResult(StreamFilterAsync(traceFilterForRpc)));
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter)
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

            IAsyncEnumerable<ParityLikeTxTrace> txTraces = StreamBlockViaChannelAsync(
                parentSearch.Object, block,
                static (writer, ct) => new ChannelParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.Rewards, writer, ct),
                static (tracer, b, blockTracer) => tracer.Execute(b, blockTracer));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(
                new ParityTxTraceFromStoreStreamingResult(ToStoreTracesAsync(txTraces)));
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
            List<ParityTxTraceFromStore> traces = new();
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

            IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentSearch.Object!, block, new(txHash, ParityTraceTypes.Trace));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
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

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(baseBlock);
            ITracer tracer2 = env.Component;

            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            tracer2.Execute(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        /// <summary>
        /// Runs block execution on a thread-pool thread, feeding each completed tx-trace into a
        /// bounded channel as it is produced. The caller consumes the channel as an
        /// <see cref="IAsyncEnumerable{T}"/>, creating end-to-end backpressure so that at most
        /// one trace is held in memory at a time.
        /// </summary>
        private async IAsyncEnumerable<ParityLikeTxTrace> StreamBlockViaChannelAsync(
            BlockHeader envHeader,
            Block block,
            Func<ChannelWriter<ParityLikeTxTrace>, CancellationToken, ChannelParityLikeBlockTracer> tracerFactory,
            Action<ITracer, Block, IBlockTracer> runBlock,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Channel<ParityLikeTxTrace> channel = Channel.CreateBounded<ParityLikeTxTrace>(
                new BoundedChannelOptions(1) { SingleWriter = true, SingleReader = true });

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task producer = Task.Run(() =>
            {
                try
                {
                    using Scope<ITracer> env = tracerEnv.BuildAndOverride(envHeader);
                    using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
                    using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, timeout.Token);
                    ChannelParityLikeBlockTracer tracer = tracerFactory(channel.Writer, combined.Token);
                    runBlock(env.Component, block, tracer.WithCancellation(combined.Token));
                }
                catch (Exception ex)
                {
                    // Propagate producer errors to the consumer via the channel so ReadAllAsync throws.
                    channel.Writer.TryComplete(ex);
                }
            }, linkedCts.Token);

            try
            {
                await foreach (ParityLikeTxTrace trace in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    yield return trace;
                }
            }
            finally
            {
                // Unblock the producer if the consumer exits early (client disconnect, exception,
                // or early termination). SuppressThrowing because errors were already propagated
                // through the channel to the consumer.
                await linkedCts.CancelAsync();
                await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        private async IAsyncEnumerable<ParityTxTraceFromStore> StreamFilterAsync(
            TraceFilterForRpc traceFilterForRpc,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IEnumerable<SearchResult<Block>> blocksSearch = blockFinder.SearchForBlocksOnMainChain(
                traceFilterForRpc.FromBlock ?? BlockParameter.Latest,
                traceFilterForRpc.ToBlock ?? BlockParameter.Latest);

            TxTraceFilter txTracerFilter = new(
                traceFilterForRpc.FromAddress,
                traceFilterForRpc.ToAddress,
                traceFilterForRpc.After,
                traceFilterForRpc.Count);

            foreach (SearchResult<Block> blockSearch in blocksSearch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (blockSearch.IsError) yield break;

                Block block = blockSearch.Object!;
                if (!blockchainBridge.HasStateForBlock(block.Header)) yield break;

                SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                if (parentSearch.IsError) yield break;
                if (!blockchainBridge.HasStateForBlock(parentSearch.Object)) yield break;

                IReadOnlyCollection<ParityLikeTxTrace> blockTraces = ExecuteBlock(
                    parentSearch.Object, block, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));

                foreach (ParityTxTraceFromStore t in txTracerFilter.FilterTxTraces(ParityTxTraceFromStore.FromTxTrace(blockTraces)))
                {
                    yield return t;
                }
                // blockTraces is eligible for GC after this block's iteration.
            }
        }

        private static async IAsyncEnumerable<ParityTxTraceFromStore> ToStoreTracesAsync(
            IAsyncEnumerable<ParityLikeTxTrace> source,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (ParityLikeTxTrace trace in TaskAsyncEnumerableExtensions.WithCancellation(source, cancellationToken))
            {
                foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
                    yield return item;
            }
        }

        private static async IAsyncEnumerable<ParityTxTraceFromReplay> ToReplayTracesAsync(
            IAsyncEnumerable<ParityLikeTxTrace> source,
            bool includeTransactionHash = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (ParityLikeTxTrace trace in TaskAsyncEnumerableExtensions.WithCancellation(source, cancellationToken))
                yield return new ParityTxTraceFromReplay(trace, includeTransactionHash);
        }

        private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

        private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
            jsonRpcConfig.BuildTimeoutCancellationToken();

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) => new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new ParityStyleSimulateBlockTracerFactory(types: GetParityTypes(traceTypes ?? ["Trace"])), _secondsPerSlot)
                .Execute(payload, blockParameter);
    }
}
