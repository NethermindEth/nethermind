// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
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
    public class TraceRpcModule : ITraceRpcModule
    {
        private readonly IReceiptFinder _receiptFinder;
        private readonly ITracer _tracer;
        private readonly IBlockFinder _blockFinder;
        private readonly TxDecoder _txDecoder = new();
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly TimeSpan _cancellationTokenTimeout;

        public TraceRpcModule(IReceiptFinder? receiptFinder, ITracer? tracer, IBlockFinder? blockFinder, IJsonRpcConfig? jsonRpcConfig, ISpecProvider? specProvider, ILogManager? logManager)
        {
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
            _cancellationTokenTimeout = TimeSpan.FromMilliseconds(_jsonRpcConfig.Timeout);
        }

        public static ParityTraceTypes GetParityTypes(string[] types) =>
            types.Select(s => FastEnum.Parse<ParityTraceTypes>(s, true)).Aggregate((t1, t2) => t1 | t2);

        /// <summary>
        /// Traces one transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null)
        {
            blockParameter ??= BlockParameter.Latest;
            call.EnsureDefaults(_jsonRpcConfig.GasCap);

            Transaction tx = call.ToTransaction();

            return TraceTx(tx, traceTypes, blockParameter);
        }

        /// <summary>
        /// Traces list of transactions. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TransactionForRpcWithTraceTypes[] calls, BlockParameter? blockParameter = null)
        {
            blockParameter ??= BlockParameter.Latest;

            SearchResult<BlockHeader> headerSearch = SearchBlockHeaderForTraceCall(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(headerSearch);
            }

            Dictionary<Keccak, ParityTraceTypes> traceTypeByTransaction = new(calls.Length);
            Transaction[] txs = new Transaction[calls.Length];
            for (int i = 0; i < calls.Length; i++)
            {
                calls[i].Transaction.EnsureDefaults(_jsonRpcConfig.GasCap);
                Transaction tx = calls[i].Transaction.ToTransaction();
                tx.Hash = new Keccak(new UInt256((ulong)i).ToBigEndian());
                ParityTraceTypes traceTypes = GetParityTypes(calls[i].TraceTypes);
                txs[i] = tx;
                traceTypeByTransaction.Add(tx.Hash, traceTypes);
            }

            Block block = new(headerSearch.Object!, txs, Enumerable.Empty<BlockHeader>());
            IReadOnlyCollection<ParityLikeTxTrace>? traces = TraceBlock(block, new ParityLikeBlockTracer(traceTypeByTransaction));
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(traces.Select(t => new ParityTxTraceFromReplay(t)));
        }

        /// <summary>
        /// Traces one raw transaction. Doesn't charge fees.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            Transaction tx = _txDecoder.Decode(new RlpStream(data), RlpBehaviors.SkipTypedWrapping);
            return TraceTx(tx, traceTypes, BlockParameter.Latest);
        }

        private SearchResult<BlockHeader> SearchBlockHeaderForTraceCall(BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return headerSearch;
            }

            BlockHeader header = headerSearch.Object;
            if (header!.IsGenesis)
            {
                UInt256 baseFee = header.BaseFeePerGas;
                header = new BlockHeader(
                    header.Hash!,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    header.Difficulty,
                    header.Number + 1,
                    header.GasLimit,
                    header.Timestamp + 1,
                    header.ExtraData,
                    header.BlobGasUsed,
                    header.ExcessBlobGas);

                header.TotalDifficulty = 2 * header.Difficulty;
                header.BaseFeePerGas = baseFee;
            }

            return new SearchResult<BlockHeader>(header);
        }

        private ResultWrapper<ParityTxTraceFromReplay> TraceTx(Transaction tx, string[] traceTypes, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> headerSearch = SearchBlockHeaderForTraceCall(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);
            }

            Block block = new(headerSearch.Object!, new[] { tx }, Enumerable.Empty<BlockHeader>());

            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlock(block, new(traceTypes1));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Keccak txHash, string[] traceTypes)
        {
            SearchResult<Keccak> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
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

            return trace_replayBlock(block, traceTypes);
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockRLP(byte[] blockRlp, string[] traceTypes)
        {
            Block block = Rlp.Decode<Block>(blockRlp);
            block.Header.TotalDifficulty ??= UInt256.One;
            return trace_replayBlock(block, traceTypes);
        }

        private ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlock(Block block, string[] traceTypes)
        {
            ParityTraceTypes traceTypes1 = GetParityTypes(traceTypes);
            IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(block, new(traceTypes1));

            // ReSharper disable once CoVariantArrayConversion
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(txTraces.Select(t => new ParityTxTraceFromReplay(t, true)));
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
                IReadOnlyCollection<ParityLikeTxTrace> txTracesFromOneBlock = ExecuteBlock(block!, new((ParityTraceTypes)(ParityTraceTypes.Trace | ParityTraceTypes.Rewards)));
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
            IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlock(block, new((ParityTraceTypes)(ParityTraceTypes.Trace | ParityTraceTypes.Rewards)));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Keccak txHash, long[] positions)
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
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Keccak txHash)
        {
            SearchResult<Keccak> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
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

            IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(block, new(txHash, ParityTraceTypes.Trace));
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using CancellationTokenSource cancellationTokenSource = new(_cancellationTokenTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            _tracer.Trace(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using CancellationTokenSource cancellationTokenSource = new(_cancellationTokenTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            _tracer.Execute(block, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }
    }
}
