// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
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
        IReceiptFinder? receiptFinder,
        ITracer? tracer,
        IBlockFinder? blockFinder,
        IJsonRpcConfig? jsonRpcConfig,
        IStateReader? stateReader,
        IOverridableTxProcessorSource? env,
        IBlockchainBridge? blockchainBridge,
        ulong? secondsPerSlot)
        : ITraceRpcModule
    {
        private readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        private readonly ITracer _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
        private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly IJsonRpcConfig _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        private readonly IOverridableTxProcessorSource _env = env ?? throw new ArgumentNullException(nameof(env));
        private readonly IBlockchainBridge _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        private readonly ulong _secondsPerSlot = secondsPerSlot ?? throw new ArgumentNullException(nameof(secondsPerSlot));

        public TraceRpcModule(IReceiptFinder? receiptFinder, ITracer? tracer, IBlockFinder? blockFinder, IJsonRpcConfig? jsonRpcConfig, OverridableTxProcessingEnv? env, IBlockchainBridge? blockchainBridge, ulong? secondsPerSlot)
            : this(receiptFinder, tracer, blockFinder, jsonRpcConfig, env?.StateReader, env, blockchainBridge, secondsPerSlot) { }

        public static ParityTraceTypes GetParityTypes(string[] types) =>
            types.Select(static s => FastEnum.Parse<ParityTraceTypes>(s, true)).Aggregate(static (t1, t2) => t1 | t2);

        /// <summary>
        /// Traces one transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null)
        {
            blockParameter ??= BlockParameter.Latest;
            call.EnsureDefaults(_jsonRpcConfig.GasCap);

            Transaction tx = call.ToTransaction();

            return TraceTx(tx, traceTypes, blockParameter, stateOverride);
        }

        /// <summary>
        /// Traces list of transactions. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TransactionForRpcWithTraceTypes[] calls, BlockParameter? blockParameter = null)
        {
            blockParameter ??= BlockParameter.Latest;

            SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!;
            if (!_stateReader.HasStateForBlock(header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(header);
            }

            Dictionary<Hash256, ParityTraceTypes> traceTypeByTransaction = new(calls.Length);
            Transaction[] txs = new Transaction[calls.Length];
            for (int i = 0; i < calls.Length; i++)
            {
                calls[i].Transaction.EnsureDefaults(_jsonRpcConfig.GasCap);
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
            SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!.Clone();
            Block block = new(header, [tx], []);

            using IOverridableTxProcessingScope? scope = _env.BuildAndOverride(header, stateOverride);

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlock(block, new(traceTypes1));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Hash256 txHash, string[] traceTypes)
        {
            SearchResult<Hash256> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            if (!_stateReader.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay>(block.Header);
            }

            IReadOnlyCollection<ParityLikeTxTrace>? txTrace = ExecuteBlock(block, new ParityLikeBlockTracer(txHash, GetParityTypes(traceTypes)));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(txTrace));
        }

        /// <summary>
        /// Traces one block. As it replays existing block will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
        {
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_stateReader.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(block.Header);
            }

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(block, new(traceTypes1));

            // ReSharper disable once CoVariantArrayConversion
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(txTraces.Select(static t => new ParityTxTraceFromReplay(t, true)));
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            List<ParityLikeTxTrace> txTraces = new();
            IEnumerable<SearchResult<Block>> blocksSearch = _blockFinder.SearchForBlocksOnMainChain(
                traceFilterForRpc.FromBlock ?? BlockParameter.Latest,
                traceFilterForRpc.ToBlock ?? BlockParameter.Latest);

            foreach (SearchResult<Block> blockSearch in blocksSearch)
            {
                if (blockSearch.IsError)
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
                }

                Block block = blockSearch.Object;
                if (!_stateReader.HasStateForBlock(block.Header))
                {
                    return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
                }

                IReadOnlyCollection<ParityLikeTxTrace> txTracesFromOneBlock = ExecuteBlock(block!, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));
                txTraces.AddRange(txTracesFromOneBlock);
            }

            IEnumerable<ParityTxTraceFromStore> txTracesResult = txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);

            TxTraceFilter txTracerFilter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(txTracerFilter.FilterTxTraces(txTracesResult));
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter)
        {
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_stateReader.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
            }

            IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(block, new(ParityTraceTypes.Trace | ParityTraceTypes.Rewards));
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
            SearchResult<Hash256> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_stateReader.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
            }

            IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(block, new(txHash, ParityTraceTypes.Trace));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            _tracer.Trace(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            _tracer.Execute(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

        private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
            _jsonRpcConfig.BuildTimeoutCancellationToken();

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null)
        {
            return new SimulateTxExecutor<ParityLikeTxTrace>(_blockchainBridge, _blockFinder, _jsonRpcConfig, new ParityStyleSimulateBlockTracerFactory(types: GetParityTypes(traceTypes ?? ["Trace"])), _secondsPerSlot)
                .Execute(payload, blockParameter);
        }
    }
}
