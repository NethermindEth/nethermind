// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public sealed class CallAnalyzerTxTracer : TxTracer
{
    private readonly DisposableResettableList<Address> _buffer;
    private readonly CallStatsAnalyzer _callStatsAnalyzer;
    private readonly CancellationToken _ct;
    private readonly SortOrder _sort;
    private StatsProcessingQueue<Address>? _queue;

    public CallAnalyzerTxTracer(DisposableResettableList<Address> buffer,
        CallStatsAnalyzer callStatsAnalyzer, SortOrder sort, CancellationToken ct)
    {
        _callStatsAnalyzer = callStatsAnalyzer;
        _buffer = buffer;
        _queue = new StatsProcessingQueue<Address>(buffer, (CallStatsAnalyzer)callStatsAnalyzer, ct);
        _ct = ct;
        _sort = sort;
        IsTracingActions = true;
    }


    private void DisposeQueue()
    {
        using (var q = _queue)
        {
            _queue = null;
            _queue = new StatsProcessingQueue<Address>(_buffer, _callStatsAnalyzer, _ct);
            q?.Dispose();
        }
    }

    public CallAnalyzerTxTrace BuildResult()
    {
        DisposeQueue();
        CallAnalyzerTxTrace trace = new();
        var stats = _callStatsAnalyzer.Stats;
        if (_sort == SortOrder.Ascending) stats = _callStatsAnalyzer.StatsAscending;

        foreach (var stat in _callStatsAnalyzer.Stats)
        {
            var entry = new CallAnalyzerTraceEntry
            {
                Address = stat.Address.ToString(),
                Count = stat.Count
            };
            trace.Entries.Add(entry);
        }


        if (_sort == SortOrder.Descending)
        {
            var sortedEntries = trace.Entries.OrderByDescending(e => e.Count).ToList();
            trace.Entries = sortedEntries;
        }

        return trace;
    }


    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!isPrecompileCall && new[]
                    { ExecutionType.CALL, ExecutionType.STATICCALL, ExecutionType.CALLCODE, ExecutionType.DELEGATECALL }
                .Contains(callType)) _queue?.Enqueue(to);
    }
}
