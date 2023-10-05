// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Newtonsoft.Json.Linq;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeJavascriptTxTracer: GethLikeTxTracer<GethTxTraceEntry>
{
    private readonly V8ScriptEngine _engine = new();
    private readonly dynamic _tracer;
    private readonly IWorldState _worldState;
    private readonly GethJavascriptStyleLog _customTraceEntry;
    private readonly GethJavascriptStyleCTX _ctx;
    private readonly List<byte> _memory = new List<byte>();

    [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor")]
    public GethLikeJavascriptTxTracer(IWorldState worldState, GethTraceOptions options) : base(options)
    {
        _customTraceEntry = new(_engine);
        _ctx = new(_engine);
        _engine.Execute(LoadJavascriptCode(options.Tracer));
        _tracer = _engine.Script.tracer;
        _worldState = worldState;
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
    }

    private string LoadJavascriptCode(string tracer) => "tracer = " + (tracer.StartsWith("{") && tracer.EndsWith("}") ? tracer : LoadJavascriptCodeFromFile(tracer));

    private string LoadJavascriptCodeFromFile(string tracerFileName)
    {
        if (!tracerFileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            tracerFileName += ".js";
        }

        tracerFileName = "Data/JSTracers/" + tracerFileName;
        string jsCode = File.ReadAllText(tracerFileName.GetApplicationResourcePath());
        return jsCode;
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
            Step(_customTraceEntry, _worldState);
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
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();
        trace.CustomTracerResult = _tracer.result(_customTraceEntry.ctx, _worldState);
        dynamic result = trace.CustomTracerResult;
        // Console.WriteLine("this is the result {0}", JArray.FromObject(result ));
        return trace;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType,
        bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _customTraceEntry.contract = new GethJavascriptStyleLog.Contract(_engine, from, to, value, input);
        _customTraceEntry.ctx = new GethJavascriptStyleLog.CTX(_engine, callType.ToString(), from, to, input, value, gas,0,0,0,0 , new byte[0], DateTime.Now.ToString());
    }

    public override void SetOperationMemory(IEnumerable<string> memoryTrace)
    {
        List<string> memoryTraceList = memoryTrace.ToList();
        base.SetOperationMemory(memoryTraceList);
        string continuousMemory = "";
        for (int item = 0; item < memoryTraceList.Count; item++)
        {
            continuousMemory += memoryTraceList[item] ;
        }
        Console.WriteLine("Continuous Memory:");
        Console.WriteLine(continuousMemory);
        _customTraceEntry.memory = new GethJavascriptStyleLog.JSMemory(continuousMemory);
    }

    // public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    // {
    //     _memory.EnsureCapacity((int)(offset + data.Length));
    //     if (offset < 0 || offset + data.Length > _memory.Capacity)
    //     {
    //         Console.WriteLine("this is the offset :{0}", offset);
    //         Console.WriteLine("this is the length of the data :{0}", data.Length);
    //         Console.WriteLine("this is the capacity of the memory :{0}", _memory.Capacity);
    //         // Handle the error condition, e.g., throw an exception or log an error.
    //         throw new ArgumentOutOfRangeException("offset", "Invalid memory access");
    //     }
    //     data.CopyTo(CollectionsMarshal.AsSpan(_memory).Slice((int)offset, (int)data.Length));
    //     base.ReportMemoryChange(offset, in data);
    // }

    public override void SetOperationStack(List<string> stackTrace)
    {
        base.SetOperationStack(stackTrace);
        _customTraceEntry.stack = new GethJavascriptStyleLog.JSStack(_engine,stackTrace);
        Console.WriteLine("this is the length of the stack :{0}", _customTraceEntry.stack.length());
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
