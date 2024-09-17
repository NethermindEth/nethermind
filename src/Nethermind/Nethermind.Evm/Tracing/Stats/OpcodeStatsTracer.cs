// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using System.IO.Abstractions;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsTracer : BlockTracerBase<OpcodeStatsTxTrace, OpcodeStatsTxTracer>
{

    // private readonly Block _block;
    long _initialBlock = 0;
    long _currentBlock = 0;
    private OpcodeStatsTxTracer _tracer;
    private int _bufferSize;
    private OpcodeStatsQueue _queue;
    private StatsAnalyzer _statsAnalyzer;
    private McsLock _processingLock = new();


    public OpcodeStatsTracer(int bufferSize, StatsAnalyzer statsAnalyzer) : base()
    {
        _bufferSize=bufferSize;
        _statsAnalyzer = statsAnalyzer;

        //_queue = new(bufferSize,_statsAnalyzer, _processingLock);
        _queue = new OpcodeStatsQueue(_bufferSize, statsAnalyzer, _processingLock);
        _tracer = new OpcodeStatsTxTracer(_queue,statsAnalyzer);
      //  _serializerOptions.Converters.Add(new GethLikeTxTraceJsonLinesConverter());
    }

    private void resetQueue()
    {
        _queue = new OpcodeStatsQueue(_bufferSize, _statsAnalyzer, _processingLock);
    }


    public override void EndTxTrace()
    {
        _queue.Enqueue(NGrams.RESET);
    }

    public override void EndBlockTrace()
    {
        base.EndBlockTrace();
        resetQueue();
        OpcodeStatsTxTrace trace = _tracer.BuildResult();
        trace.InitialBlockNumber = _initialBlock;
        trace.CurrentBlockNumber = _currentBlock;
        AddTrace(_tracer.BuildResult());
    }

    public OpcodeStatsTxTracer StartNewTxTrace(Transaction? tx) => _tracer;

    public override void StartNewBlockTrace(Block block)
    {
        base.StartNewBlockTrace(block);
        var number = block.Header.Number;
        if (_initialBlock == 0)
            _initialBlock = number;
        _currentBlock = number;

    }


    protected override OpcodeStatsTxTracer OnStart(Transaction? tx)
    {
        return _tracer;
    }


    protected override OpcodeStatsTxTrace OnEnd(OpcodeStatsTxTracer txTracer)
    {
        throw new NotImplementedException();
    }
}
