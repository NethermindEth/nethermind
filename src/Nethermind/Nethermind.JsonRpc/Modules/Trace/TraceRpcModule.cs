// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
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
using Nethermind.State;

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
        IStateReader stateReader,
        IBlockchainBridge blockchainBridge,
        IBlocksConfig blocksConfig,
        ISpecProvider specProvider)
        : ITraceRpcModule
    {
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly ISpecProvider _specProvider = specProvider;

        public static ParityTraceTypes GetParityTypes(string[] types) =>
            types.Select(static s => FastEnum.Parse<ParityTraceTypes>(s, true)).Aggregate(static (t1, t2) => t1 | t2);

        /// <summary>
        /// Traces one transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null)
        {
            blockParameter ??= BlockParameter.Latest;
            call.EnsureDefaults(jsonRpcConfig.GasCap);

            Transaction tx = call.ToTransaction();

            return TraceTx(tx, traceTypes, blockParameter, stateOverride);
        }

        /// <summary>
        /// Traces list of transactions. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TransactionForRpcWithTraceTypes[] calls, BlockParameter? blockParameter = null)
        {
            blockParameter ??= BlockParameter.Latest;

            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!;
            if (!stateReader.HasStateForBlock(header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(header);
            }

            Dictionary<Hash256, ParityTraceTypes> traceTypeByTransaction = new(calls.Length);
            Transaction[] txs = new Transaction[calls.Length];
            for (int i = 0; i < calls.Length; i++)
            {
                calls[i].Transaction.EnsureDefaults(jsonRpcConfig.GasCap);
                Transaction tx = calls[i].Transaction.ToTransaction();
                tx.Hash = new Hash256(new UInt256((ulong)i).ToValueHash());
                ParityTraceTypes traceTypes = GetParityTypes(calls[i].TraceTypes);
                txs[i] = tx;
                traceTypeByTransaction.Add(tx.Hash, traceTypes);
            }

            Block block = new(header, txs, []);
            IReadOnlyCollection<ParityLikeTxTrace>? traces = TraceBlock(block, new(traceTypeByTransaction));
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(traces.Select(static t => new ParityTxTraceFromReplay(t)));
        }

        /// <summary>
        /// Traces one raw transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            Transaction tx = _txDecoder.Decode(new RlpStream(data), RlpBehaviors.SkipTypedWrapping);
            return TraceTx(tx, traceTypes, BlockParameter.Latest);
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

            using var env = tracerEnv.BuildAndOverride(header, stateOverride);
            ITracer tracer = env.Component;

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlockDirect(tracer, block, new(traceTypes1));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Hash256 txHash, string[] traceTypes)
        {
            SearchResult<Hash256> blockHashSearch = receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
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

            if (!stateReader.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay>(parentSearch.Object);
            }

            IReadOnlyCollection<ParityLikeTxTrace>? txTrace = ExecuteBlock(parentSearch.Object, block, new ParityLikeBlockTracer(txHash, GetParityTypes(traceTypes)));
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

            if (!stateReader.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(parentSearch.Object);
            }

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(parentSearch.Object, block, new(traceTypes1));

            // ReSharper disable once CoVariantArrayConversion
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(txTraces.Select(static t => new ParityTxTraceFromReplay(t, true)));
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            List<ParityLikeTxTrace> txTraces = new();
            IEnumerable<SearchResult<Block>> blocksSearch = blockFinder.SearchForBlocksOnMainChain(
                traceFilterForRpc.FromBlock ?? BlockParameter.Latest,
                traceFilterForRpc.ToBlock ?? BlockParameter.Latest);

            foreach (SearchResult<Block> blockSearch in blocksSearch)
            {
                if (blockSearch.IsError)
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
                }

                Block block = blockSearch.Object;
                if (!stateReader.HasStateForBlock(block?.Header))
                {
                    return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
                }

                SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                if (parentSearch.IsError)
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
                }

                if (!stateReader.HasStateForBlock(parentSearch.Object))
                {
                    return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
                }

                IReadOnlyCollection<ParityLikeTxTrace> txTracesFromOneBlock = ExecuteBlock(parentSearch.Object, block!, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));
                txTraces.AddRange(txTracesFromOneBlock);
            }

            IEnumerable<ParityTxTraceFromStore> txTracesResult = txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);

            TxTraceFilter txTracerFilter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(txTracerFilter.FilterTxTraces(txTracesResult));
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, ForkActivationParameter? forkActivation = null)
        {
            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!stateReader.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
            }
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!stateReader.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
            }

            IReadOnlyCollection<ParityLikeTxTrace> txTraces = forkActivation != null
                ? ExecuteBlockWithForkOverride(parentSearch.Object, block, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards), forkActivation)
                : ExecuteBlock(parentSearch.Object, block, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
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
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Hash256 txHash)
        {
            SearchResult<Hash256> blockHashSearch = receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
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

            if (!stateReader.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
            }

            IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentSearch.Object!, block, new(txHash, ParityTraceTypes.Trace));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using var env = tracerEnv.BuildAndOverride(block.Header);
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
            using var env = tracerEnv.BuildAndOverride(baseBlock);
            ITracer tracer2 = env.Component;

            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            tracer2.Execute(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlockWithForkOverride(BlockHeader parentHeader, Block block, ParityLikeBlockTracer tracer, ForkActivationParameter forkActivationParam)
        {
            (bool isSuccess, IReleaseSpec? resolvedSpec, ForkActivation activation, string? errorMessage) = forkActivationParam.TryResolve(_specProvider);
            if (!isSuccess)
            {
                throw new ArgumentException($"Fork resolution failed: {errorMessage}");
            }

            IReleaseSpec effectiveSpec = resolvedSpec ?? _specProvider.GetSpec(activation);
            IReleaseSpec isolatedSpec = new ReleaseSpecDecorator(effectiveSpec);

            BlockHeader effectiveHeader = block.Header;
            bool needsHeaderOverride = activation.Timestamp.HasValue || activation.BlockNumber != block.Number;

            if (needsHeaderOverride)
            {
                effectiveHeader = block.Header.Clone();
                effectiveHeader.Timestamp = activation.Timestamp ?? effectiveHeader.Timestamp;
                effectiveHeader.Number = activation.BlockNumber;
            }

            block = new(effectiveHeader, block.Transactions, block.Uncles);

            return ExecuteBlock(parentHeader, block, tracer);
        }

        private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

        private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
            jsonRpcConfig.BuildTimeoutCancellationToken();

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null)
        {
            return new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, new ParityStyleSimulateBlockTracerFactory(types: GetParityTypes(traceTypes ?? ["Trace"])), _secondsPerSlot)
                .Execute(payload, blockParameter);
        }
    }
}
