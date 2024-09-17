// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsTxTracer : TxTracer
{

    private StatsAnalyzer _statsAnalyzer;
    private OpcodeStatsQueue? _queue = null;

    public OpcodeStatsTxTracer(OpcodeStatsQueue queue, StatsAnalyzer statsAnalyzer)
    {
        _statsAnalyzer = statsAnalyzer;
        _queue= queue;
        IsTracingInstructions = true;
    }

    public OpcodeStatsTxTracer(int size, McsLock processingLock, StatsAnalyzer statsAnalyzer)
    {
        _statsAnalyzer = statsAnalyzer;
        _queue=new(size, statsAnalyzer, processingLock);
        IsTracingInstructions = true;
    }

    private void postProcess(StatsAnalyzer statsAnalyzer)
    {
    }

    private void DisposeQueue()
    {
        using (var q = _queue)
        {
            _queue=null;
        }
    }

    public OpcodeStatsTxTrace BuildResult()
    {
        DisposeQueue();
        OpcodeStatsTxTrace trace = new();
        trace.Confidence = _statsAnalyzer.Confidence;
        trace.ErrorPerItem = _statsAnalyzer.Error;
        foreach ((ulong topN, ulong count) pattern in _statsAnalyzer.topNQueue.UnorderedItems)
        {
            NGrams _ngram = new NGrams(pattern.topN);
            OpcodeStatsTraceEntry entry = new();
            entry.Pattern = _ngram.ToString();
            entry.Bytes = _ngram.ToBytes();
            entry.Count = pattern.count;
            trace.Entries.Add(entry);
        }

        var sortedEntries = trace.Entries.OrderByDescending(e => e.Count).ToList();
        trace.Entries = sortedEntries;
        return trace;
    }



    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
      _queue.Enqueue(opcode);
    }
}

