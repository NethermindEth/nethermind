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
    protected int _bufferSize;
    private StatsAnalyzer _statsAnalyzer;
    private McsLock _processingLock = new();
    private HashSet<Instruction> _ignore;

    private static readonly object _lock = new object();

    public OpcodeStatsTracer(int bufferSize, StatsAnalyzer statsAnalyzer, HashSet<Instruction> ignore) : base()
    {
        _bufferSize = bufferSize;
        _statsAnalyzer = statsAnalyzer;
        _ignore = ignore;
        _tracer = new OpcodeStatsTxTracer(_ignore, _bufferSize, _processingLock, _statsAnalyzer);
    }

    private void resetQueue()
    {
    }


    public override void EndTxTrace()
    {
        _tracer.AddTxEndMarker();
    }

    public override void EndBlockTrace()
    {
        OpcodeStatsTxTracer tracer;

        long InitialBlockNumber;
        long CurrentBlockNumber;
        lock (_lock)
        {
            tracer = _tracer;
            InitialBlockNumber = _initialBlock;
            CurrentBlockNumber = _currentBlock;
            _tracer = new OpcodeStatsTxTracer(_ignore, _bufferSize, _processingLock, _statsAnalyzer);
        }
        OpcodeStatsTxTrace trace = tracer.BuildResult();
        trace.InitialBlockNumber = InitialBlockNumber;
        trace.CurrentBlockNumber = CurrentBlockNumber;

        lock (_lock)
        {
            AddTrace(trace);
        }
    }

    public OpcodeStatsTxTracer StartNewTxTrace(Transaction? tx) => _tracer;

    public override void StartNewBlockTrace(Block block)
    {
        lock (_lock)
        {
            base.StartNewBlockTrace(block);
            var number = block.Header.Number;
            if (_initialBlock == 0)
                _initialBlock = number;
            _currentBlock = number;
        }

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
