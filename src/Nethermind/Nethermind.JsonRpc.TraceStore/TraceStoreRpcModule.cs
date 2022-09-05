//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Module for tracing using database
/// </summary>
public class TraceStoreRpcModule : ITraceRpcModule
{
    private readonly IDbWithSpan _traceStore;
    private readonly ITraceRpcModule _traceModule;
    private readonly IBlockFinder _blockFinder;
    private readonly IReceiptFinder _receiptFinder;
    private readonly ILogger _logger;

    private static readonly IDictionary<ParityTraceTypes, Action<ParityLikeTxTrace>> _filters = new Dictionary<ParityTraceTypes, Action<ParityLikeTxTrace>>
    {
        { ParityTraceTypes.Trace, FilterTrace },
        { ParityTraceTypes.StateDiff , FilterStateDiff },
        { ParityTraceTypes.VmTrace | ParityTraceTypes.Trace, FilterStateVmTrace }
    };

    public TraceStoreRpcModule(ITraceRpcModule traceModule, IDbWithSpan traceStore, IBlockFinder blockFinder, IReceiptFinder receiptFinder, ILogManager logManager)
    {
        _traceStore = traceStore;
        _traceModule = traceModule;
        _blockFinder = blockFinder;
        _receiptFinder = receiptFinder;
        _logger = logManager.GetClassLogger<TraceStoreRpcModule>();
    }

    public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null) =>
        _traceModule.trace_call(call, traceTypes, blockParameter);

    public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TransactionForRpcWithTraceTypes[] calls, BlockParameter? blockParameter = null) =>
        _traceModule.trace_callMany(calls, blockParameter);

    public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes) =>
        _traceModule.trace_rawTransaction(data, traceTypes);

    public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Keccak txHash, params string[] traceTypes) =>
        TryTraceTransaction(
            txHash,
            TraceRpcModule.GetParityTypes(traceTypes),
            static t => new ParityTxTraceFromReplay(t),
            out ResultWrapper<ParityTxTraceFromReplay>? result)
        && result is not null
            ? result
            : _traceModule.trace_replayTransaction(txHash, traceTypes);

    public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
    {
        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;

        if (TryGetBlockTraces(block, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            FilterTraces(traces, TraceRpcModule.GetParityTypes(traceTypes));
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(traces.Select(t => new ParityTxTraceFromReplay(t, true)));
        }

        return _traceModule.trace_replayBlockTransactions(blockParameter, traceTypes);
    }

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

            Block block = blockSearch.Object!;
            if (TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces) && traces is not null)
            {
                FilterTraces(traces, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
                txTraces.AddRange(traces);
            }
            else
            {
                // fallback when we miss any traces in db
                return _traceModule.trace_filter(traceFilterForRpc);
            }
        }

        IEnumerable<ParityTxTraceFromStore> txTracesResult = txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);

        TxTraceFilter txTracerFilter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);
        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(txTracerFilter.FilterTxTraces(txTracesResult));
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter)
    {
        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;
        if (TryGetBlockTraces(block, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            FilterTraces(traces, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
        }

        return _traceModule.trace_block(blockParameter);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Keccak txHash, long[] positions)
    {
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction = trace_transaction(txHash);
        List<ParityTxTraceFromStore> traces = TraceRpcModule.ExtractPositionsFromTxTrace(positions, traceTransaction);
        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Keccak txHash) =>
        TryTraceTransaction(
            txHash,
            ParityTraceTypes.Trace,
            static t => ParityTxTraceFromStore.FromTxTrace(t),
            out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? result)
        && result is not null
            ? result
            : _traceModule.trace_transaction(txHash);

    private bool TryTraceTransaction<T>(
        Keccak txHash,
        ParityTraceTypes traceTypes,
        Func<ParityLikeTxTrace, T> map,
        out ResultWrapper<T>? result)
    {
        SearchResult<Keccak> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
        if (blockHashSearch.IsError)
        {
            {
                result = ResultWrapper<T>.Fail(blockHashSearch);
                return true;
            }
        }

        SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
        if (blockSearch.IsError)
        {
            {
                result = ResultWrapper<T>.Fail(blockSearch);
                return true;
            }
        }

        Block block = blockSearch.Object!;

        if (TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            ParityLikeTxTrace? trace = GetTxTrace(block, txHash, traces);
            if (trace is not null)
            {
                FilterTrace(trace, traceTypes);
                result = ResultWrapper<T>.Success(map(trace));
                return true;
            }
        }

        result = null;
        return false;
    }

    private bool TryGetBlockTraces(BlockHeader block, out List<ParityLikeTxTrace>? traces)
    {
        Span<byte> tracesSerialized = _traceStore.GetSpan(block.Hash!);
        try
        {
            if (!tracesSerialized.IsEmpty)
            {
                List<ParityLikeTxTrace>? tracesDeserialized = TraceSerializer.Deserialize<List<ParityLikeTxTrace>>(tracesSerialized);
                if (tracesDeserialized is not null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Found persisted traces for block {block.ToString(BlockHeader.Format.FullHashAndNumber)}");
                    traces = tracesDeserialized;
                    return true;
                }
            }

            traces = null;
            return false;
        }
        finally
        {
            _traceStore.DangerousReleaseMemory(tracesSerialized);
        }
    }

    private ParityLikeTxTrace? GetTxTrace(Block block, Keccak txHash, List<ParityLikeTxTrace> traces)
    {
        int index = traces.FindIndex(t => t.TransactionHash == txHash);
        return index != -1 && index < traces.Count ? traces[index] : null;
    }

    private void FilterTraces(List<ParityLikeTxTrace> traces, ParityTraceTypes traceTypes)
    {
        for (int i = 0; i < traces.Count; i++)
        {
            ParityLikeTxTrace parityLikeTxTrace = traces[i];
            FilterTrace(parityLikeTxTrace, traceTypes);
        }

        if ((traceTypes & ParityTraceTypes.Rewards) == 0)
        {
            FilterRewards(traces);
        }
    }

    private static void FilterTrace(ParityLikeTxTrace parityLikeTxTrace, ParityTraceTypes traceTypes)
    {
        foreach (KeyValuePair<ParityTraceTypes, Action<ParityLikeTxTrace>> filter in _filters)
        {
            if ((traceTypes & filter.Key) == 0)
            {
                filter.Value(parityLikeTxTrace);
            }
        }
    }


    // VmTrace uses flags IsTracingCode, IsTracingInstructions
    private static void FilterStateVmTrace(ParityLikeTxTrace trace)
    {
        trace.VmTrace = null;
    }

    // StateDiff uses flags IsTracingState, IsTracingStorage
    private static void FilterStateDiff(ParityLikeTxTrace trace)
    {
        trace.StateChanges = null;
        if (trace.VmTrace is not null)
        {
            for (int i = 0; i < trace.VmTrace.Operations.Length; i++)
            {
                trace.VmTrace.Operations[i].Store = null!;
            }
        }
    }

    // Trace uses flags IsTracingActions, IsTracingReceipt
    private static void FilterTrace(ParityLikeTxTrace trace)
    {
        trace.Output = null;
        // trace action?
    }

    private static void FilterRewards(List<ParityLikeTxTrace> traces)
    {
        if (traces[^1].TransactionHash is null)
        {
            traces.RemoveAt(traces.Count - 1);
        }
    }
}
