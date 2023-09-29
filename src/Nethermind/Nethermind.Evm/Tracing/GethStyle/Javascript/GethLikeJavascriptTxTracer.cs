// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeJavascriptTxTracer: GethLikeTxTracer<GethTxTraceEntry>
{
    private readonly V8ScriptEngine _engine = new();
    private readonly dynamic _tracer;
    private readonly GethJavascriptStyleLog _customTraceEntry;

    [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor")]
    public GethLikeJavascriptTxTracer(GethTraceOptions options) : base(options)
    {
        _customTraceEntry = new(_engine);
        _engine.Execute(LoadJavascriptCode(options.Tracer));
        _tracer = _engine.Script.tracer;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    private string LoadJavascriptCode(string tracer) => "tracer = " + (tracer.StartsWith("{") && tracer.EndsWith("}") ? tracer : LoadJavascriptCodeFromFile(tracer));

    private string LoadJavascriptCodeFromFile(string tracerFileName)
    {
        try
        {
            if (!tracerFileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                tracerFileName += ".js";
            }
            string jsCode = File.ReadAllText("/Volumes/ethereum/ethereum/execution/nethermind/src/Nethermind/Nethermind.Evm/bin/Debug/net7.0/Tracing/GethStyle/Javascript/" + tracerFileName);
            Console.WriteLine("this is the js code {0}", jsCode);
            return jsCode;
        }
        catch (IOException e)
        {
            Console.WriteLine("An error occurred while reading the file: " + e.Message);
            return null;
        }

    }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        base.StartOperation(depth, gas, opcode, pc, isPostMerge);
        if (CurrentTraceEntry is not null)
        {
            _customTraceEntry.pc = CurrentTraceEntry.ProgramCounter;
            _customTraceEntry.op = new GethJavascriptStyleLog.OpcodeString(opcode);
            _customTraceEntry.gas = CurrentTraceEntry.Gas;
            _customTraceEntry.gasCost = CurrentTraceEntry.GasCost;
            _customTraceEntry.depth = CurrentTraceEntry.Depth;
            Step(_customTraceEntry, null);
            Console.WriteLine("this is it {0}", _customTraceEntry.op.isPush());
        }
    }

    private void Step(GethJavascriptStyleLog log, dynamic db)
    {
        try
        {
            _tracer.step(log, db);
        }
        catch (Exception ex) when (ex is IScriptEngineException)
        {
            _tracer.fault(log, db);
        }

        dynamic? result = _tracer.result(null, null);
        Trace.CustomTracerResult.Add(result);

    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType,
        bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _customTraceEntry.contract = new GethJavascriptStyleLog.Contract(_engine, from, to, value, input);
        Console.WriteLine("this is it {0}", _customTraceEntry.contract.getValue());
    }

    public override void SetOperationMemory(IEnumerable<string> memoryTrace)
    {
        List<string> memoryTraceList = memoryTrace.ToList();
        base.SetOperationMemory(memoryTraceList);
        _customTraceEntry.memory = new GethJavascriptStyleLog.JSMemory(memoryTraceList);
        Console.WriteLine("this is the lenght of the memory :{0}", _customTraceEntry.memory.length());
        for(int item =0;  item < _customTraceEntry.memory.getCount(); item++)
        {
            Console.WriteLine("item at index {0} : {1}", item, _customTraceEntry.memory.getItem(item));
            //Console.WriteLine("this is a slice : {0}",  CustomTraceEntry.memory.slice(0,1));
        }

    }
    public override void SetOperationStack(List<string> stackTrace)
    {
        base.SetOperationStack(stackTrace);
        _customTraceEntry.stack = new GethJavascriptStyleLog.JSStack(stackTrace);
        Console.WriteLine("this is the lenght of the stack :{0}", _customTraceEntry.stack.length());
        for(int item =0;  item < _customTraceEntry.stack.getCount(); item++)
        {
            Console.WriteLine("item at index {0} : {1}", item, _customTraceEntry.stack.getItem(item));
        }
    }

    public override void ReportRefund(long refund)
    {
        base.ReportRefund(refund);
        _customTraceEntry.refund = refund;
    }

}
