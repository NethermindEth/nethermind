// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public sealed class GethLikeJavaScriptTxTracer : GethLikeTxTracer
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);

    private readonly dynamic _tracer;
    private readonly Log _log = new();
    private readonly Engine _engine;
    private readonly Db _db;
    private readonly CallFrame _frame = new();
    private readonly FrameResult _result = new();
    private readonly CancellationTokenSource _cts;
    private readonly IDisposable _ctsRegistration;
    private bool _engineReleased;
    private bool _disposed;
    private Stack<ulong>? _frameGas;
    private Stack<Log.Contract>? _contracts;
    private int _depth = -1;

    // Context is updated only of first ReportAction call.
    private readonly Context _ctx;
    private readonly TracerFunctions _functions;

    public GethLikeJavaScriptTxTracer(
        Engine engine,
        Db db,
        Context ctx,
        GethTraceOptions options) : base(options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _engine = engine;
        _db = db;
        _ctx = ctx;

        _tracer = engine.CreateTracer(options.Tracer);
        _functions = GetAvailableFunctions(((IDictionary<string, object>)_tracer).Keys);
        if (_functions.HasFlag(TracerFunctions.setup))
        {
            _tracer.setup(options.TracerConfig?.ToString() ?? "{}");
        }

        TimeSpan timeout = options.Timeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), timeout, $"Tracer timeout must be between 1ns and {MaxTimeout.TotalMinutes}m.");
        _cts = new CancellationTokenSource(timeout);
        _ctsRegistration = _cts.Token.Register(static e => ((Engine)e!).Interrupt(), engine);
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.TxHash = _ctx.TxHash;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = MaterializeResult(_tracer.result(_ctx, _db)) };
        _ctsRegistration.Dispose();
        ReleaseEngine();

        return result;
    }

    // The script result is converted to JSON while its engine is alive, so the engine can go right after
    // and the trace keeps nothing in the V8 heap.
    private static JsonElement MaterializeResult(object? scriptResult)
    {
        NumberConversion previousConversion = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Raw;
        try
        {
            return JsonSerializer.SerializeToElement(scriptResult, EthereumJsonSerializer.JsonOptions);
        }
        finally
        {
            ForcedNumberConversion.Value = previousConversion;
        }
    }

    // The tracer proxy roots its script object in the V8 heap until it is disposed, whatever happens to the engine.
    private void ReleaseEngine()
    {
        if (_engineReleased)
        {
            return;
        }

        _engineReleased = true;
        ((object)_tracer as IDisposable)?.Dispose();
        _engine.Dispose();
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        _depth++;

        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        bool isAnyCreate = callType.IsAnyCreate();
        if (_depth == 0)
        {
            _ctx.type = isAnyCreate ? "CREATE" : "CALL";
            _ctx.From = from;
            _ctx.To = to;
            _ctx.Input = input;
            _ctx.Value = value;
        }
        else
        {
            // Always track the parent frame contract so it can be restored on frame exit,
            // even when the tracer defines no enter/exit callbacks.
            _contracts ??= new Stack<Log.Contract>();
            _contracts.Push(_log.contract);

            if (_functions.HasFlag(TracerFunctions.enter))
            {
                _frame.From = from;
                _frame.To = to;
                _frame.Input = input;
                _frame.Value = callType == ExecutionType.STATICCALL ? null : value;
                _frame.Gas = gas;
                _frame.Type = callType.FastToString();
                _tracer.enter(_frame);
                _frameGas ??= new Stack<ulong>();
                _frameGas.Push(gas);
            }
        }

        _log.contract = callType == ExecutionType.DELEGATECALL
            ? new Log.Contract(_log.contract.Caller, from, value, input)
            : new Log.Contract(from, to, value, isAnyCreate ? null : input);
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _log.pc = pc;
        _log.op = new Log.Opcode(opcode);
        _log.gas = gas;
        _log.depth = env.GetGethTraceDepth();
        _log.error = null;
        _log.gasCost = null;
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        _log.gasCost ??= _log.gas - gas;
        if (_functions.HasFlag(TracerFunctions.postStep))
        {
            _tracer.postStep(_log, _db);
        }
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        _log.error = error.GetEvmExceptionDescription();
        _tracer.fault(_log, _db);
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);

        _ctx.To ??= deploymentAddress;
        InvokeExit(gas, deployedCode);
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        InvokeExit(gas, output);
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        InvokeExit(gasLeft, output, EvmExceptionType.Revert.GetEvmExceptionDescription());
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        InvokeExit(0, Array.Empty<byte>(), evmExceptionType.GetEvmExceptionDescription());
    }

    private void InvokeExit(ulong gas, ReadOnlyMemory<byte> output, string? error = null)
    {
        if (_contracts?.TryPop(out Log.Contract contract) == true)
        {
            _log.contract = contract;
        }

        if (_functions.HasFlag(TracerFunctions.exit) && _frameGas?.Count > 0)
        {
            _result.GasUsed = _frameGas.Pop() - gas;
            _result.Output = output.ToArray();
            _result.Error = error;
            _tracer.exit(_result);
        }

        _depth--;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        _ctx.gasUsed = gasSpent.SpentGas;
        _ctx.Output = output;
        _ctx.error = error;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        _ctx.gasUsed = gasSpent.SpentGas;
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

        if (_functions.HasFlag(TracerFunctions.step))
        {
            _tracer.step(_log, _db);
        }

        if (_log.op?.Value == Instruction.REVERT)
        {
            ReportOperationError(EvmExceptionType.Revert);
        }
    }

    public override void ReportRefund(long refund)
    {
        base.ReportRefund(refund);
        _log.refund += refund;
    }

    private static TracerFunctions GetAvailableFunctions(ICollection<string> functions)
    {
        const TracerFunctions required = TracerFunctions.result;

        TracerFunctions result = TracerFunctions.none;

        // skip none
        foreach (TracerFunctions function in FastEnum.GetValues<TracerFunctions>().Skip(1))
        {
            string name = FastEnum.GetName(function);
            if (functions.Contains(name))
            {
                result |= function;
            }
            else if (function <= required)
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

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        base.Dispose();
        _ctsRegistration.Dispose();
        _cts.Dispose();
        ReleaseEngine();
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
        postStep = 32,
        setup = 64
    }
}
