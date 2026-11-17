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
    ParityTraceTypes storedTraceTypes,
    IJsonRpcConfig jsonRpcConfig,
    ILogManager logManager,
    int parallelization = 0) : ITraceRpcModule
{
    private readonly IDb _traceStore = traceStore;
    private readonly ITraceRpcModule _traceModule = traceModule;
    private readonly IBlockFinder _blockFinder = blockFinder;
    private readonly IReceiptFinder _receiptFinder = receiptFinder;
    private readonly ITraceSerializer<ParityLikeTxTrace> _traceSerializer = traceSerializer;
    private readonly ParityTraceTypes _storedTraceTypes = storedTraceTypes;
    private readonly IJsonRpcConfig _jsonRpcConfig = jsonRpcConfig;
    private readonly int _parallelization = parallelization;
    private readonly ILogger _logger = logManager.GetClassLogger<TraceStoreRpcModule>();

    private static readonly IDictionary<ParityTraceTypes, Action<ParityLikeTxTrace>> _filters = new Dictionary<ParityTraceTypes, Action<ParityLikeTxTrace>>
    {
        { ParityTraceTypes.Trace, FilterTrace },
        { ParityTraceTypes.StateDiff , FilterStateDiff },
        { ParityTraceTypes.VmTrace, FilterVmTrace }
    };

    // What live trace_block and trace_filter trace.
    private const ParityTraceTypes BlockTraceTypes = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
    private const int StoredTraceReadWindow = 64;

    // The store can only answer for what it records; anything else falls back to live tracing.
    private bool IsStored(ParityTraceTypes traceTypes) => (traceTypes & ~_storedTraceTypes) == ParityTraceTypes.None;

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

    public ResultWrapper<ParityTxTraceFromReplay?> trace_replayTransaction(Hash256 txHash, string[] traceTypes, bool traceNonCanonical = false)
    {
        if (TraceRpcModule.TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes)
            && TryGetStoredTrace(txHash, parityTypes, traceNonCanonical, out ParityLikeTxTrace? storedTrace) && storedTrace is not null)
        {
            return BuildStoreStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    ParityReplayEnvelopeWriter.WriteFromTrace(writer, storedTrace, includeTxHash: true, EthereumJsonSerializer.JsonOptions);
                    FlushPipe(writer, pipeWriter, ct);
                },
                runBuffered: () => new ParityTxTraceFromReplay(storedTrace, includeTransactionHash: true))!;
        }

        return _traceModule.trace_replayTransaction(txHash, traceTypes, traceNonCanonical);
    }

    private bool TryGetStoredTrace(Hash256 txHash, ParityTraceTypes traceTypes, bool traceNonCanonical, out ParityLikeTxTrace? trace)
    {
        // Live replay can answer a transaction with its block's reward, which a stored transaction trace never holds.
        if ((traceTypes & ParityTraceTypes.Rewards) != ParityTraceTypes.None || !IsStored(traceTypes))
        {
            trace = null;
            return false;
        }

        SearchResult<Hash256> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
        if (blockHashSearch.IsError)
        {
            trace = null;
            return false;
        }

        // The receipt index can still point at a block a reorg replaced; live replay refuses it unless asked not to.
        SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!, requireCanonical: !traceNonCanonical));
        if (blockSearch.IsError)
        {
            trace = null;
            return false;
        }

        Block block = blockSearch.Object!;
        if (TryGetBlockTraces(block.Header.Hash!, out List<ParityLikeTxTrace>? traces) && traces is not null)
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
        // The pending tag resolves to the head, whose stored traces are not a pending block's.
        if (TraceRpcModule.IsPending(blockParameter))
        {
            return TraceRpcModule.PendingNotSupported<IEnumerable<ParityTxTraceFromReplay>>();
        }

        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter, unknownBlockErrorCode: ErrorCodes.StandardResourceNotFound);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;

        if (TraceRpcModule.TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes) && IsStored(parityTypes)
            && TryGetBlockTraces(block.Hash!, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            FilterTraces(traces, parityTypes);

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
        (BlockParameter fromBlock, BlockParameter toBlock) = traceFilterForRpc.GetBlockRange();
        if (TraceRpcModule.IsPending(fromBlock) || TraceRpcModule.IsPending(toBlock))
        {
            return TraceRpcModule.PendingNotSupported<IEnumerable<ParityTxTraceFromStore>>();
        }

        if (_blockFinder.IsRangeInFuture(fromBlock, toBlock))
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(BlockFinderExtensions.BlockRangeInFuture, ErrorCodes.InvalidParams);
        }

        if (!IsStored(BlockTraceTypes))
        {
            return _traceModule.trace_filter(traceFilterForRpc);
        }

        List<Hash256> blockHashes = [];
        foreach (SearchResult<BlockHeader> blockSearch in _blockFinder.SearchForHeadersOnMainChain(fromBlock, toBlock, ErrorCodes.StandardResourceNotFound))
        {
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Hash256 blockHash = blockSearch.Object!.Hash!;
            if (!_traceStore.KeyExists(blockHash))
            {
                return _traceModule.trace_filter(traceFilterForRpc);
            }

            blockHashes.Add(blockHash);
        }

        TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After ?? 0, traceFilterForRpc.Count, traceFilterForRpc.Mode);

        if (blockHashes.Count == 0)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(filter.FilterTxTraces([]));
        }

        return BuildStoreStreamingResult<ParityTxTraceFromStore>(
            runStreaming: (writer, pipeWriter, ct) =>
            {
                foreach ((Hash256 blockHash, List<ParityLikeTxTrace>? blockTraces) in ReadStoredBlockTraces(blockHashes, filter))
                {
                    if (filter.IsExhausted) break;
                    if (blockTraces is null)
                    {
                        foreach (ParityTxTraceFromStore liveTrace in filter.FilterTxTraces(TraceBlockLive(blockHash)))
                        {
                            ct.ThrowIfCancellationRequested();
                            TypeInfoJsonSerializer.Serialize(writer, liveTrace, EthereumJsonSerializer.JsonOptions);
                            FlushPipe(writer, pipeWriter, ct);
                        }

                        continue;
                    }

                    foreach (ParityLikeTxTrace trace in blockTraces)
                    {
                        ct.ThrowIfCancellationRequested();
                        ParityTxTraceFromStore.Write(writer, trace, filter, EthereumJsonSerializer.JsonOptions);
                        FlushPipe(writer, pipeWriter, ct);
                    }
                }
            },
            runBuffered: () => filter.FilterTxTraces(ReadBlockTraceItems(blockHashes, filter)));
    }

    private IEnumerable<ParityTxTraceFromStore> ReadBlockTraceItems(List<Hash256> blockHashes, TxTraceFilter filter)
    {
        foreach ((Hash256 blockHash, List<ParityLikeTxTrace>? blockTraces) in ReadStoredBlockTraces(blockHashes, filter))
        {
            foreach (ParityTxTraceFromStore item in blockTraces is null ? TraceBlockLive(blockHash) : FlattenStoreItems(blockTraces))
            {
                yield return item;
            }
        }
    }

    private IEnumerable<ParityTxTraceFromStore> TraceBlockLive(Hash256 blockHash)
    {
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = _traceModule.trace_block(new BlockParameter(blockHash));
        if (result.Result.ResultType == Core.ResultType.Failure || result.Data is null)
        {
            throw new ResourceNotFoundException(result.Result.Error ?? $"Traces unavailable for block {blockHash}");
        }

        using (result.Data as IDisposable)
        {
            foreach (ParityTxTraceFromStore item in result.Data)
            {
                yield return item;
            }
        }
    }

    private IEnumerable<(Hash256 BlockHash, List<ParityLikeTxTrace>? Traces)> ReadStoredBlockTraces(List<Hash256> blockHashes, TxTraceFilter filter)
    {
        foreach (Hash256[] window in blockHashes.Chunk(StoredTraceReadWindow))
        {
            if (filter.IsExhausted) yield break;

            List<ParityLikeTxTrace>?[] windowTraces = _parallelization switch
            {
                1 => window.Select(ReadStoredTraces).ToArray(),
                0 => window.AsParallel().AsOrdered().Select(ReadStoredTraces).ToArray(),
                int n => window.AsParallel().AsOrdered().WithDegreeOfParallelism(n).Select(ReadStoredTraces).ToArray()
            };

            for (int i = 0; i < window.Length; i++)
            {
                yield return (window[i], windowTraces[i]);
            }
        }
    }

    private List<ParityLikeTxTrace>? ReadStoredTraces(Hash256 blockHash) =>
        TryGetBlockTraces(blockHash, out List<ParityLikeTxTrace>? traces) ? traces : null;

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
    {
        if (TraceRpcModule.IsPending(blockParameter))
        {
            return TraceRpcModule.PendingNotSupported<IEnumerable<ParityTxTraceFromStore>>();
        }

        // Fork overrides require live re-execution; bypass the store and delegate directly.
        if (fork is not null)
            return _traceModule.trace_block(blockParameter, fork);

        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter, unknownBlockErrorCode: ErrorCodes.StandardResourceNotFound);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;
        if (IsStored(BlockTraceTypes) && TryGetBlockTraces(block.Hash!, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            return BuildStoreStreamingResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    foreach (ParityLikeTxTrace trace in traces)
                    {
                        ct.ThrowIfCancellationRequested();
                        ParityTxTraceFromStore.Write(writer, trace, null, EthereumJsonSerializer.JsonOptions);
                        FlushPipe(writer, pipeWriter, ct);
                    }
                },
                runBuffered: () => FlattenStoreItems(traces));
        }

        return _traceModule.trace_block(blockParameter);
    }

    public ResultWrapper<ParityTxTraceFromStore?> trace_get(Hash256 txHash, long[] traceAddress) =>
        TraceRpcModule.SelectTraceAddress(trace_transaction(txHash), traceAddress);

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> trace_transaction(Hash256 txHash, bool traceNonCanonical = false)
    {
        if (TryGetStoredTrace(txHash, ParityTraceTypes.Trace, traceNonCanonical, out ParityLikeTxTrace? storedTrace) && storedTrace is not null)
        {
            return BuildStoreStreamingResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    ParityTxTraceFromStore.Write(writer, storedTrace, null, EthereumJsonSerializer.JsonOptions, ct);
                    FlushPipe(writer, pipeWriter, ct);
                },
                runBuffered: () => ParityTxTraceFromStore.FromTxTrace(storedTrace))!;
        }

        return _traceModule.trace_transaction(txHash, traceNonCanonical);
    }

    private bool TryGetBlockTraces(Hash256 blockHash, out List<ParityLikeTxTrace>? traces)
    {
        Span<byte> tracesSerialized = _traceStore.GetSpan(blockHash);
        try
        {
            if (!tracesSerialized.IsEmpty)
            {
                List<ParityLikeTxTrace>? tracesDeserialized = _traceSerializer.Deserialize(tracesSerialized);
                if (tracesDeserialized is not null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Found persisted traces for block {blockHash}");
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
        if ((traceTypes & ParityTraceTypes.Rewards) == 0)
        {
            FilterRewards(traces);
        }

        for (int i = 0; i < traces.Count; i++)
        {
            ParityLikeTxTrace parityLikeTxTrace = traces[i];
            FilterTrace(parityLikeTxTrace, traceTypes);
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


    // VmTrace uses flags IsTracingCode, IsTracingInstructions; the latter also records each operation's ex.store.
    private static void FilterVmTrace(ParityLikeTxTrace trace) => trace.VmTrace = null;

    // StateDiff uses flags IsTracingState, IsTracingStorage
    private static void FilterStateDiff(ParityLikeTxTrace trace) => trace.StateChanges = null;

    // A reward entry survives FilterRewards only when rewards are requested; its action is the reward itself, as in live replay.
    private static void FilterTrace(ParityLikeTxTrace trace)
    {
        if (!IsReward(trace))
        {
            trace.Action = null;
        }
    }

    private static bool IsReward(ParityLikeTxTrace trace) => trace.TransactionHash is null && trace.Action?.Type == "reward";

    private static void FilterRewards(List<ParityLikeTxTrace> traces)
    {
        for (int i = traces.Count - 1; i >= 0; i--)
        {
            ParityLikeTxTrace trace = traces[i];
            if (IsReward(trace))
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
