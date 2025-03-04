// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer;

public class PatternAnalyzerTxTracer : TxTracer
{

    private StatsAnalyzer _statsAnalyzer;
    private OpcodeStatsQueue? _queue = null;
    private HashSet<Instruction> _ignoreSet;
    private McsLock _processingLock;
    DisposableResettableList<Instruction> _buffer;
    //  public OpcodeStatsTxTracer(OpcodeStatsTracer blockTracer, OpcodeStatsQueue queue, StatsAnalyzer statsAnalyzer)
    //  {
    //      _statsAnalyzer = statsAnalyzer;
    //      _queue= queue;
    //      _blockTracer = blockTracer;
    //      IsTracingInstructions = true;
    //  }

    public PatternAnalyzerTxTracer(DisposableResettableList<Instruction> buffer, HashSet<Instruction> ignoreSet, int size, McsLock processingLock, StatsAnalyzer statsAnalyzer)
    {
        _ignoreSet = ignoreSet;
        _statsAnalyzer = statsAnalyzer;
        _processingLock = processingLock;
        _buffer = buffer;
        _queue = new(buffer, (StatsAnalyzer)statsAnalyzer);
        IsTracingInstructions = true;
    }


    public void AddTxEndMarker()
    {
        _queue?.Enqueue(NGrams.RESET);
    }

    private void DisposeQueue()
    {
        using (var q = _queue)
        {
            _queue = null;
            _queue = new(_buffer, _statsAnalyzer);
            q?.Dispose();
        }
    }

    public PatternAnalyzerTxTrace BuildResult()
    {
        DisposeQueue();
        PatternAnalyzerTxTrace trace = new();
        trace.Confidence = _statsAnalyzer.Confidence;
        trace.ErrorPerItem = _statsAnalyzer.Error;
        foreach ((ulong topN, ulong count) pattern in _statsAnalyzer.topNQueue.UnorderedItems)
        {
            NGrams _ngram = new NGrams(pattern.topN);
            PatternAnalyzerTraceEntry entry = new PatternAnalyzerTraceEntry
            {
                Pattern = _ngram.ToString(),
                Bytes = _ngram.ToBytes(),
                Count = pattern.count
            };
        //    entry.Pattern = _ngram.ToString();
        //    entry.Bytes = _ngram.ToBytes();
        //    entry.Count = pattern.count;
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

