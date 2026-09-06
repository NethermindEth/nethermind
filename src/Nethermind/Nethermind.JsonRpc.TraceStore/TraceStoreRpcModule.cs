// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Module for tracing using database
/// </summary>
public class TraceStoreRpcModule(ITraceRpcModule traceModule,
    IDb traceStore,
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    ITraceSerializer<ParityLikeTxTrace> traceSerializer,
    IJsonRpcConfig jsonRpcConfig,
    ILogManager logManager,
    int parallelization = 0) : ITraceRpcModule
{
    private readonly IDb _traceStore = traceStore;
    private readonly ITraceRpcModule _traceModule = traceModule;
    private readonly IBlockFinder _blockFinder = blockFinder;
    private readonly IReceiptFinder _receiptFinder = receiptFinder;
    private readonly ITraceSerializer<ParityLikeTxTrace> _traceSerializer = traceSerializer;
    private readonly IJsonRpcConfig _jsonRpcConfig = jsonRpcConfig;
    private readonly int _parallelization = parallelization;
    private readonly ILogger _logger = logManager.GetClassLogger<TraceStoreRpcModule>();

    private static readonly IDictionary<ParityTraceTypes, Action<ParityLikeTxTrace>> _filters = new Dictionary<ParityTraceTypes, Action<ParityLikeTxTrace>>
    {
        { ParityTraceTypes.Trace, FilterTrace },
        { ParityTraceTypes.StateDiff , FilterStateDiff },
        { ParityTraceTypes.VmTrace | ParityTraceTypes.Trace, FilterStateVmTrace }
    };

    private static IEnumerable<ParityTxTraceFromStore> FlattenStoreItems(List<List<ParityLikeTxTrace>> blockTraces) =>
        blockTraces.SelectMany(static block => block.SelectMany(ParityTxTraceFromStore.FromTxTrace));

    private static IEnumerable<ParityTxTraceFromStore> FlattenStoreItems(List<ParityLikeTxTrace> traces) =>
        traces.SelectMany(ParityTxTraceFromStore.FromTxTrace);

    private static void FlushPipe(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken ct)
    {
        if (pipeWriter is null) return;
        writer.Flush();
        pipeWriter.FlushAsync(ct).SafeWait();
    }

    private ResultWrapper<IEnumerable<T>> BuildStoreStreamingResult<T>(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
        Func<IEnumerable<T>> runBuffered)
    {
        if (!_jsonRpcConfig.EnableTracingStreamMode)
        {
            return ResultWrapper<IEnumerable<T>>.Success(runBuffered());
        }

        CancellationTokenSource timeoutCts = _jsonRpcConfig.BuildTimeoutCancellationToken();
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

    public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null) =>
        _traceModule.trace_call(call, traceTypes, blockParameter, stateOverride);

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) => _traceModule.trace_simulateV1(payload, blockParameter, traceTypes);

    public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TraceCallManyRequest calls, BlockParameter? blockParameter = null) =>
        _traceModule.trace_callMany(calls, blockParameter);

    public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes) =>
        _traceModule.trace_rawTransaction(data, traceTypes);

    public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Hash256 txHash, string[] traceTypes, bool traceNonCanonical = false)
    {
        if (TryGetStoredTrace(txHash, TraceRpcModule.GetParityTypes(traceTypes), out ParityLikeTxTrace? storedTrace) && storedTrace is not null)
        {
            return BuildStoreStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    ParityReplayEnvelopeWriter.WriteFromTrace(writer, storedTrace, includeTxHash: true, EthereumJsonSerializer.JsonOptions);
                    FlushPipe(writer, pipeWriter, ct);
                },
                runBuffered: () => new ParityTxTraceFromReplay(storedTrace, includeTransactionHash: true));
        }

        return _traceModule.trace_replayTransaction(txHash, traceTypes, traceNonCanonical);
    }

    private bool TryGetStoredTrace(Hash256 txHash, ParityTraceTypes traceTypes, out ParityLikeTxTrace? trace)
    {
        SearchResult<Hash256> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
        if (blockHashSearch.IsError)
        {
            trace = null;
            return false;
        }

        SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
        if (blockSearch.IsError)
        {
            trace = null;
            return false;
        }

        Block block = blockSearch.Object!;
        if (TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            ParityLikeTxTrace? hit = GetTxTrace(txHash, traces);
            if (hit is not null)
            {
                FilterTrace(hit, traceTypes);
                trace = hit;
                return true;
            }
        }

        trace = null;
        return false;
    }

    private ResultWrapper<ParityTxTraceFromReplay> BuildStoreStreamingSingleResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
        Func<ParityTxTraceFromReplay> runBuffered)
    {
        if (!_jsonRpcConfig.EnableTracingStreamMode)
        {
            return ResultWrapper<ParityTxTraceFromReplay>.Success(runBuffered());
        }

        CancellationTokenSource timeoutCts = _jsonRpcConfig.BuildTimeoutCancellationToken();
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

            return BuildStoreStreamingResult<ParityTxTraceFromReplay>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    foreach (ParityLikeTxTrace t in traces)
                    {
                        ct.ThrowIfCancellationRequested();
                        ParityReplayEnvelopeWriter.WriteFromTrace(writer, t, includeTxHash: true, EthereumJsonSerializer.JsonOptions);
                        FlushPipe(writer, pipeWriter, ct);
                    }
                },
                runBuffered: () => traces.Select(static t => new ParityTxTraceFromReplay(t, true)));
        }

        return _traceModule.trace_replayBlockTransactions(blockParameter, traceTypes);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
    {
        IEnumerable<SearchResult<Block>> blocksSearch = _blockFinder.SearchForBlocksOnMainChain(
            traceFilterForRpc.FromBlock ?? BlockParameter.Latest,
            traceFilterForRpc.ToBlock ?? BlockParameter.Latest);

        IEnumerable<(SearchResult<Block> BlockSearch, List<ParityLikeTxTrace>? Traces)> blockResults = _parallelization switch
        {
            0 => blocksSearch.AsParallel().AsOrdered().Select(GetBlockTraces),
            1 => blocksSearch.Select(GetBlockTraces),
            var n => blocksSearch.AsParallel().AsOrdered().WithDegreeOfParallelism(n).Select(GetBlockTraces)
        };

        List<List<ParityLikeTxTrace>>? blockTraces = null;
        foreach ((SearchResult<Block> blockSearch, List<ParityLikeTxTrace>? traces) in blockResults)
        {
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            if (traces is null)
            {
                // fallback when we miss any traces in db
                return _traceModule.trace_filter(traceFilterForRpc);
            }

            (blockTraces ??= []).Add(traces);
        }

        TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);

        if (blockTraces is null)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(filter.FilterTxTraces([]));
        }

        return BuildStoreStreamingResult<ParityTxTraceFromStore>(
            runStreaming: (writer, pipeWriter, ct) =>
            {
                foreach (List<ParityLikeTxTrace> blockTracesOne in blockTraces)
                {
                    foreach (ParityLikeTxTrace trace in blockTracesOne)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
                        {
                            if (!filter.ShouldUseTxTrace(item.Action)) continue;
                            JsonSerializer.Serialize(writer, item, EthereumJsonSerializer.JsonOptions);
                        }
                        FlushPipe(writer, pipeWriter, ct);
                    }
                }
            },
            runBuffered: () => filter.FilterTxTraces(FlattenStoreItems(blockTraces)));

        (SearchResult<Block> BlockSearch, List<ParityLikeTxTrace>? Traces) GetBlockTraces(SearchResult<Block> blockSearch)
        {
            if (blockSearch.IsError)
            {
                return (blockSearch, null);
            }

            Block block = blockSearch.Object!;
            TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces);
            return (blockSearch, traces);
        }
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
    {
        // Fork overrides require live re-execution; bypass the store and delegate directly.
        if (fork is not null)
            return _traceModule.trace_block(blockParameter, fork);

        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;
        if (TryGetBlockTraces(block, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            return BuildStoreStreamingResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    foreach (ParityLikeTxTrace trace in traces)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
                        {
                            JsonSerializer.Serialize(writer, item, EthereumJsonSerializer.JsonOptions);
                        }
                        FlushPipe(writer, pipeWriter, ct);
                    }
                },
                runBuffered: () => FlattenStoreItems(traces));
        }

        return _traceModule.trace_block(blockParameter);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Hash256 txHash, long[] positions)
    {
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction = trace_transaction(txHash);
        List<ParityTxTraceFromStore> traces = TraceRpcModule.ExtractPositionsFromTxTrace(positions, traceTransaction);
        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Hash256 txHash, bool traceNonCanonical = false)
    {
        if (TryGetStoredTrace(txHash, ParityTraceTypes.Trace, out ParityLikeTxTrace? storedTrace) && storedTrace is not null)
        {
            return BuildStoreStreamingResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(storedTrace))
                    {
                        ct.ThrowIfCancellationRequested();
                        JsonSerializer.Serialize(writer, item, EthereumJsonSerializer.JsonOptions);
                    }
                    FlushPipe(writer, pipeWriter, ct);
                },
                runBuffered: () => ParityTxTraceFromStore.FromTxTrace(storedTrace));
        }

        return _traceModule.trace_transaction(txHash, traceNonCanonical);
    }

    private bool TryGetBlockTraces(BlockHeader block, out List<ParityLikeTxTrace>? traces)
    {
        Span<byte> tracesSerialized = _traceStore.GetSpan(block.Hash!);
        try
        {
            if (!tracesSerialized.IsEmpty)
            {
                List<ParityLikeTxTrace>? tracesDeserialized = _traceSerializer.Deserialize(tracesSerialized);
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

    private static ParityLikeTxTrace? GetTxTrace(Hash256 txHash, List<ParityLikeTxTrace> traces)
    {
        int index = traces.FindIndex(t => t.TransactionHash == txHash);
        return index != -1 ? traces[index] : null;
    }

    private static void FilterTraces(List<ParityLikeTxTrace> traces, ParityTraceTypes traceTypes)
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
    private static void FilterStateVmTrace(ParityLikeTxTrace trace) => trace.VmTrace = null;

    // StateDiff uses flags IsTracingState, IsTracingStorage
    private static void FilterStateDiff(ParityLikeTxTrace trace)
    {
        trace.StateChanges = null;
        if (trace.VmTrace is not null)
        {
            for (int i = 0; i < trace.VmTrace.Operations.Count; i++)
            {
                trace.VmTrace.Operations[i].Store = null!;
            }
        }
    }

    // Trace uses flags IsTracingActions, IsTracingReceipt
    private static void FilterTrace(ParityLikeTxTrace trace) => trace.Output = null;// trace action?

    private static void FilterRewards(List<ParityLikeTxTrace> traces)
    {
        for (int i = traces.Count - 1; i >= 0; i--)
        {
            ParityLikeTxTrace trace = traces[i];
            if (trace.TransactionHash is null && trace.Action?.Type == "reward")
            {
                traces.RemoveAt(i);
            }
            else
            {
                break;
            }
        }
    }
}
