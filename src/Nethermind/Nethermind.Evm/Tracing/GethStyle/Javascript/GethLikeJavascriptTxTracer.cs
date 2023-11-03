// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FastEnumUtility;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public sealed class GethLikeJavascriptTxTracer : GethLikeTxTracer
{
    private readonly dynamic _tracer;
    private readonly Log _log = new();
    private readonly V8ScriptEngine _engine;
    private readonly Db _db;
    private readonly CallFrame _frame = new();
    private readonly FrameResult _result = new();
    private int _depth;

    // Context is updated only of first ReportAction call.
    private readonly Context _ctx;
    private readonly TracerFunctions _functions;

    public GethLikeJavascriptTxTracer(
        V8ScriptEngine engine,
        Db db,
        Context ctx,
        GethTraceOptions options) : base(options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;

        _engine = engine;
        _db = db;
        _ctx = ctx;
        engine.Execute(LoadJavascriptCode(options.Tracer));
        _tracer = engine.Script.tracer;
        _functions = GetAvailableFunctions(((IDictionary<string, object>)_tracer).Keys);
        if (_functions.HasFlag(TracerFunctions.setup))
        {
            _tracer.setup(options.TracerConfig);
        }
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

    protected override GethLikeTxTrace CreateTrace() => new(_engine);

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        result.CustomTracerResult = _tracer.result(_ctx, _db);
        return result;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        _log.contract = new Log.Contract(from, to, value, input);
        BigInteger valueBigInt = (BigInteger)value;
        if (callType == ExecutionType.TRANSACTION)
        {
            _ctx.type = callType.IsAnyCreate() ? "CREATE" : "CALL";
            _ctx.From = from;
            _ctx.To = to;
            _ctx.Input = input;
            _ctx.value = valueBigInt;
            _ctx.gas = gas;
        }
        else if (_functions.HasFlag(TracerFunctions.enter))
        {
            _frame.From = from;
            _frame.To = to;
            _frame.Input = input;
            _frame.Value = valueBigInt;
            _frame.Gas = gas;
            _frame.Type = callType.FastToString();
            _tracer.enter(_frame);
        }

        _depth++;
    }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        _log.pc = pc;
        _log.op = new Log.Opcode(opcode);
        _log.gas = gas;
        _log.depth = depth;
        _log.error = null;
        if (_functions.HasFlag(TracerFunctions.step))
        {
            _tracer.step(_log, _db);
        }
    }

    public override void ReportOperationRemainingGas(long gas)
    {
        _log.gasCost = _log.gas - gas;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        _log.error = GetErrorDescription(error);
        _tracer.fault(_log, _db);
    }

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);

        _ctx.To ??= deploymentAddress;
        InvokeExit(gas, deployedCode);
    }

    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        InvokeExit(gas, output);
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        InvokeExit(0, Array.Empty<byte>(), GetErrorDescription(evmExceptionType));
    }

    private void InvokeExit(long gas, ReadOnlyMemory<byte> output, string? error = null)
    {
        _depth--;
        if (_depth > 0 && _functions.HasFlag(TracerFunctions.exit))
        {
            _result.GasUsed = gas;
            _result.Output = output.ToArray();
            _result.Error = error;
            _tracer.exit(_result);
        }
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        _ctx.gasUsed = gasSpent;
        _ctx.Output = output;
        _ctx.error = error;
    }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        _ctx.gasUsed = gasSpent;
        _ctx.Output = output;
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        base.SetOperationMemory(memoryTrace);
        _log.memory.MemoryTrace = memoryTrace;
    }

    public override void SetOperationStack(TraceStack stack)
    {
        base.SetOperationStack(stack);
        _log.stack = new Log.Stack(stack);
    }

    public override void ReportRefund(long refund)
    {
        base.ReportRefund(refund);
        _log.refund = refund;
    }

    private const TracerFunctions Required = TracerFunctions.result;

    private TracerFunctions GetAvailableFunctions(ICollection<string> functions)
    {
        TracerFunctions result = TracerFunctions.none;

        // skip none
        foreach (TracerFunctions function in FastEnum.GetValues<TracerFunctions>().Skip(1))
        {
            string name = FastEnum.GetName(function);
            if (functions.Contains(name))
            {
                result |= function;
            }
            else if (function <= Required)
            {
                throw new ArgumentException($"trace object must expose required function {name}");
            }
        }

        if (result.HasFlag(TracerFunctions.enter) != result.HasFlag(TracerFunctions.exit))
        {
            throw new ArgumentException("trace object must expose either both or none of enter() and exit()");
        }

        return result;
    }

    // ReSharper disable InconsistentNaming
    [Flags]
    private enum TracerFunctions : byte
    {
        none = 0,
        fault = 1,
        result = 2,
        enter = 4,
        exit = 8,
        step = 16,
        setup = 32
    }
}
