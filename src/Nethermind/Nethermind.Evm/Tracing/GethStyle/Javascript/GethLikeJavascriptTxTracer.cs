// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using FastEnumUtility;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public sealed class GethLikeJavascriptTxTracer : GethLikeTxTracer
{
    // private readonly V8ScriptEngine _engine = new(V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart | V8ScriptEngineFlags.EnableDebugging);
    private readonly V8ScriptEngine _engine = new();
    private readonly dynamic _tracer;
    private readonly Log _log;
    private readonly List<byte> _memory = new();
    private readonly Db _db;
    private readonly CallFrame _frame;
    private readonly FrameResult _result;

    // Context is updated only of first ReportAction call.
    private readonly Context _ctx;
    private readonly bool _enterExit;

    public GethLikeJavascriptTxTracer(Hash256 txHash,
        Db db,
        Context ctx,
        IReleaseSpec spec,
        GethTraceOptions options) : base(options)
    {
        _db = db;
        _ctx = ctx;
        _db.Engine = _engine;
        _log = new() { memory = new Log.Memory(_engine, _memory) };
        _frame = new CallFrame(_engine);
        _result = new FrameResult(_engine);
        _engine.Execute(BigIntegerJS.Code);
        _engine.AddHostObject("toWord", new Func<object, object>(bytes => bytes.ToWord()?.ToScriptArray(_engine)));
        _engine.AddHostObject("toHex", new Func<IList, string>(bytes => bytes.ToHexString()));
        _engine.AddHostObject("toAddress", new Func<object, object>(address => address.ToAddress().Bytes.ToScriptArray(_engine)));
        _engine.AddHostObject("isPrecompiled", new Func<object, bool>(address => address.ToAddress().IsPrecompile(spec)));
        _engine.AddHostObject("slice", new Func<IList, long, long, ScriptObject>(((input, start, end) => input.Slice(start, end).ToScriptArray(_engine))));
        _engine.AddHostObject("toContract", new Func<object, ulong, ScriptObject>(((from, nonce) => ContractAddress.From(from.ToAddress(), nonce).Bytes.ToScriptArray(_engine))));
        _engine.AddHostObject("toContract2", new Func<object, string, object, ScriptObject>(((from, salt, initcode) =>
            ContractAddress.From(from.ToAddress(), Bytes.FromHexString(salt), ValueKeccak.Compute(initcode.ToBytes()).Bytes).Bytes.ToScriptArray(_engine))));
        _engine.Execute(LoadJavascriptCode(options.Tracer));
        _tracer = _engine.Script.tracer;
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        _ctx.txHash = txHash.BytesToArray().ToScriptArray(_engine);

        IDictionary<string, object> functionsDictionary = (IDictionary<string, object>)_tracer;
        if (functionsDictionary.ContainsKey("setup"))
        {
            _tracer.setup(options.TracerConfig);
        }

        bool enter = functionsDictionary.ContainsKey("enter");
        bool exit = functionsDictionary.ContainsKey("exit");
        if (enter != exit)
        {
            throw new ArgumentException("trace object must expose either both or none of enter() and exit()");
        }

        _enterExit = enter;
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
        _log.pc = pc;
        _log.op = new Log.Opcode(opcode);
        _log.gas = gas;
        _log.depth = depth;

        try
        {
            _tracer.step(_log, _db);
        }
        catch (Exception ex) when (ex is IScriptEngineException)
        {
            _tracer.fault(_log, _db);
        }
    }

    public override void ReportOperationRemainingGas(long gas) => _log.gasCost = _log.gas - gas;

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();
        trace.CustomTracerResult = _tracer.result(_ctx, _db);
        return trace;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        _log.contract = new Log.Contract(_engine, from, to, value, input);
        BigInteger valueBigInt = (BigInteger)value;
        if (callType == ExecutionType.TRANSACTION)
        {
            _ctx.type = callType.IsAnyCreate() ? "CREATE" : "CALL";
            _ctx.from = from.Bytes.ToScriptArray(_engine);
            _ctx.to = to.Bytes.ToScriptArray(_engine);
            _ctx.input = input.ToArray().ToScriptArray(_engine);
            _ctx.value = valueBigInt;
            _ctx.gas = gas;
        }
        else if (_enterExit)
        {
            _frame.From = from;
            _frame.To = to;
            _frame.Input = input.ToArray();
            _frame.Value = valueBigInt;
            _frame.Gas = gas;
            _frame.Type = callType.FastToString();
            _tracer.enter(_frame);
        }
    }

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);

        _ctx.to ??= deploymentAddress.Bytes.ToScriptArray(_engine);
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
        if (_enterExit)
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
        _ctx.output = output?.ToScriptArray(_engine);
    }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        _ctx.gasUsed = gasSpent;
        _ctx.output = output.ToScriptArray(_engine);
    }

    public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        base.ReportMemoryChange(offset, data);

        _memory.EnsureCapacity((int)(offset + data.Length));
        if (_memory.Count < offset + data.Length)
        {
            _memory.AddRange(Enumerable.Repeat((byte)0, (int)(offset + data.Length) - _memory.Count));
        }

        if (offset < 0 || offset + data.Length > _memory.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Invalid memory access");
        }

        data.CopyTo(CollectionsMarshal.AsSpan(_memory).Slice((int)offset, (int)data.Length));
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);

        _log.error = GetErrorDescription(error);
    }

    public override void SetOperationStack(TraceStack stack)
    {
        base.SetOperationStack(stack);
        _log.stack = new Log.Stack(stack);
    }

    public override void ReportRefund(long refund)
    {
        _log.refund = refund;
    }
}
