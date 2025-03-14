// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer;

public sealed class PatternAnalyzerTxTracer : TxTracer
{
    private readonly DisposableResettableList<Instruction> _buffer;
    private readonly CancellationToken _ct;
    private readonly HashSet<Instruction> _ignoreSet;
    private readonly SortOrder _sort;
    private readonly StatsAnalyzer _statsAnalyzer;
    private McsLock _processingLock;
    private StatsProcessingQueue? _queue;

    public PatternAnalyzerTxTracer(DisposableResettableList<Instruction> buffer, HashSet<Instruction> ignoreSet,
        int size, McsLock processingLock, StatsAnalyzer statsAnalyzer, SortOrder sort, CancellationToken ct)
    {
        _ignoreSet = ignoreSet;
        _statsAnalyzer = statsAnalyzer;
        _processingLock = processingLock;
        _buffer = buffer;
        _queue = new StatsProcessingQueue(buffer, (StatsAnalyzer)statsAnalyzer, ct);
        _ct = ct;
        _sort = sort;
        IsTracingInstructions = true;
    }


    public void AddTxEndMarker()
    {
        _queue?.Enqueue(NGram.Reset);
    }

    private void DisposeQueue()
    {
        using (var q = _queue)
        {
            _queue = null;
            _queue = new StatsProcessingQueue(_buffer, _statsAnalyzer, _ct);
            q?.Dispose();
        }
    }

    public PatternAnalyzerTxTrace BuildResult()
    {
        DisposeQueue();
        PatternAnalyzerTxTrace trace = new();
        trace.Confidence = _statsAnalyzer.Confidence;
        trace.ErrorPerItem = _statsAnalyzer.Error;
        var stats = _statsAnalyzer.Stats;
        if (_sort == SortOrder.Ascending) stats = _statsAnalyzer.StatsAscending;

        foreach (var stat in _statsAnalyzer.Stats)
        {
            var entry = new PatternAnalyzerTraceEntry
            {
                Pattern = stat.Ngram.ToString(),
                Bytes = stat.Ngram.ToBytes(),
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


    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        if (!_ignoreSet.Contains(opcode)) _queue?.Enqueue(opcode);
    }
}
