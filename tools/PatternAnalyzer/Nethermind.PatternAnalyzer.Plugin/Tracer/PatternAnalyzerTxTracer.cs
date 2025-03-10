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
    private readonly HashSet<Instruction> _ignoreSet;
    private readonly StatsAnalyzer _statsAnalyzer;
    private McsLock _processingLock;
    private StatsProcessingQueue? _queue;
    private CancellationToken _ct;

    public PatternAnalyzerTxTracer(DisposableResettableList<Instruction> buffer, HashSet<Instruction> ignoreSet,
        int size, McsLock processingLock, StatsAnalyzer statsAnalyzer, CancellationToken ct)
    {
        _ignoreSet = ignoreSet;
        _statsAnalyzer = statsAnalyzer;
        _processingLock = processingLock;
        _buffer = buffer;
        _queue = new StatsProcessingQueue(buffer, (StatsAnalyzer)statsAnalyzer, ct);
        _ct = ct;
        IsTracingInstructions = true;
    }


    public void AddTxEndMarker()
    {
        _queue?.Enqueue(NGram.RESET);
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
        foreach (var stat in _statsAnalyzer.Stats)
        {
            var entry = new PatternAnalyzerTraceEntry
            {
                Pattern = stat.ngram.ToString(),
                Bytes = stat.ngram.ToBytes(),
                Count = stat.count
            };
            trace.Entries.Add(entry);
        }

        var sortedEntries = trace.Entries.OrderByDescending(e => e.Count).ToList();
        trace.Entries = sortedEntries;
        return trace;
    }


    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        if (!_ignoreSet.Contains(opcode)) _queue?.Enqueue(opcode);
    }
}
