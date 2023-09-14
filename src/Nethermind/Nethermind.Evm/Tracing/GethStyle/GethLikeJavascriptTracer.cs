// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeJavascriptTracer: GethLikeTxTracer<GethJavascriptStyleLog>
{
    private readonly GethJavascriptCustomTracer _customTracer;
    public GethLikeJavascriptTracer(GethTraceOptions options) : base(options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = IsTracingFullMemory;
        _customTracer = new GethJavascriptCustomTracer(options.Tracer);

    }
    private GethJavascriptStyleLog CustomTraceEntry { get; set; } = new();
    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        base.StartOperation(depth, gas, opcode, pc, isPostMerge);
            CustomTraceEntry.pc = CurrentTraceEntry.ProgramCounter;
            CustomTraceEntry.op = new GethJavascriptStyleLog.OpcodeString(opcode);
            CustomTraceEntry.gas = CurrentTraceEntry.Gas;
            CustomTraceEntry.gasCost = CurrentTraceEntry.GasCost;
            CustomTraceEntry.depth = CurrentTraceEntry.Depth;
            _customTracer.Step(CustomTraceEntry, null);
            Console.WriteLine("this is it {0}", CustomTraceEntry.op.isPush());
    }
    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType,
        bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        CustomTraceEntry.contract = new GethJavascriptStyleLog.Contract(from, to, value, input);
        Console.WriteLine("this is it {0}", CustomTraceEntry.contract.getValue());

    }

    public override void SetOperationMemory(IEnumerable<string> memoryTrace)
    {
        base.SetOperationMemory(memoryTrace);
        CustomTraceEntry.memory = new GethJavascriptStyleLog.JSMemory(memoryTrace.ToList());
        Console.WriteLine("this is the lenght of the memory :{0}", CustomTraceEntry.memory.length());
        for(int item =0;  item < CustomTraceEntry.memory.getCount(); item++)
        {
            Console.WriteLine("item at index {0} : {1}", item, CustomTraceEntry.memory.getItem(item));
            //Console.WriteLine("this is a slice : {0}",  CustomTraceEntry.memory.slice(0,1));
        }

    }
    public override void SetOperationStack(List<string> stackTrace)
    {
        base.SetOperationStack(stackTrace);
        CustomTraceEntry.stack = new GethJavascriptStyleLog.JSStack(stackTrace);
        Console.WriteLine("this is the lenght of the stack :{0}", CustomTraceEntry.stack.length());
        for(int item =0;  item < CustomTraceEntry.stack.getCount(); item++)
        {
            Console.WriteLine("item at index {0} : {1}", item, CustomTraceEntry.stack.getItem(item));
        }
    }
    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();

        trace.CustomTracerResult.AddRange(_customTracer.CustomTracerResult);

        return trace;
    }
    public override void ReportRefund(long refund)
    {
        base.ReportRefund(refund);
        CustomTraceEntry.refund = refund;

    }
    protected override void AddTraceEntry(GethJavascriptStyleLog entry){}
    protected override GethJavascriptStyleLog CreateTraceEntry(Instruction opcode) => new();
}
