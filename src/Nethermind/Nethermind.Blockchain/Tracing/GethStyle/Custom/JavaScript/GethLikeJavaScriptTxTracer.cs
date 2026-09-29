// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using FastEnumUtility;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public sealed class GethLikeJavaScriptTxTracer : GethLikeTxTracer, ITraceOperationStart, ITraceOperationGasCost, ITraceRevertFault
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly dynamic _tracer;
    private readonly Log _log = new();
    private readonly Engine _engine;
    private readonly Db _db;
    private readonly CallFrame _frame = new();
    private readonly FrameResult _result = new();
    private readonly GethTraceDeadline? _deadline;
    private readonly CancellationTokenRegistration _ctsRegistration;
    private bool _disposed;
    private bool _failedBeforeExecution;
    private bool _pendingStep;
    private string? _rootError;
    private string? _actionErrorDetails;
    private ulong _actionRemainingGas;
    private TraceStack _operationStack;
    private Stack<ulong>? _frameGas;
    private Stack<Log.Contract>? _contracts;
    private int _depth = -1;

    // Context is updated only of first ReportAction call.
    private readonly Context _ctx;
    private readonly TracerFunctions _functions;

    /// <summary>Creates a JavaScript tracer using the supplied execution context and engine.</summary>
    public GethLikeJavaScriptTxTracer(
        Engine engine,
        Db db,
        Context ctx,
        GethTraceOptions options) : base(options, destroyRefund: engine.SelfDestructRefund)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _engine = engine;
        _db = db;
        _ctx = ctx;

        _deadline = options.ExecutionCancellation is null ? new GethTraceDeadline() : null;
        CancellationToken token = options.ExecutionCancellation ?? _deadline!.Token;
        try
        {
            _ctsRegistration = token.Register(static e => ((Engine)e!).Interrupt(), engine);
            Engine.CurrentEngine = _engine;
            _tracer = engine.CreateTracer(options.Tracer);
            _functions = GetAvailableFunctions((object)_tracer);
            if (_functions.HasFlag(TracerFunctions.setup))
            {
                Engine.CurrentEngine = _engine;
                _tracer.setup(options.TracerConfig?.ToString() ?? "{}");
            }
            _deadline?.Start(options.Timeout ?? DefaultTimeout);
        }
        catch
        {
            _ctsRegistration.Dispose();
            _deadline?.Dispose();
            throw;
        }
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.TxHash = _ctx.TxHash;
        Engine.CurrentEngine = _engine;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = MaterializeResult(_tracer.result(_ctx, _db)) };
        Dispose();

        return result;
    }

    /// <summary>
    /// Renders the script result to UTF-8 JSON while its engine is alive, so the engine can go right after and
    /// the trace keeps nothing in the V8 heap. The bytes are written to the response verbatim.
    /// </summary>
    /// <remarks>
    /// Renders with the static <see cref="EthereumJsonSerializer.JsonOptions"/>, since the request's serializer
    /// is not reachable from the tracer; the response serializer's depth limit is applied when the bytes are
    /// written, see <see cref="RenderedJsonConverter"/>.
    /// </remarks>
    private static RenderedJson MaterializeResult(object? scriptResult)
    {
        NumberConversion previousConversion = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Raw;
        try
        {
            return new RenderedJson(JsonSerializer.SerializeToUtf8Bytes(scriptResult, EthereumJsonSerializer.JsonOptions));
        }
        finally
        {
            ForcedNumberConversion.Value = previousConversion;
        }
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        _depth++;
        _actionErrorDetails = null;
        _actionRemainingGas = 0;

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
                Engine.CurrentEngine = _engine;
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
        _log.refund = CurrentRefund;
        _log.pc = pc;
        _log.op = new Log.Opcode(opcode);
        _log.gas = gas;
        _log.depth = env.GetGethTraceDepth();
        _log.error = null;
        _log.gasCost = null;
        _failedBeforeExecution = false;
        _pendingStep = opcode is Instruction.RETURNDATACOPY or Instruction.RETURN or Instruction.REVERT
            or Instruction.MLOAD or Instruction.MSTORE or Instruction.MSTORE8 or Instruction.CALLDATACOPY or Instruction.CODECOPY
            or Instruction.KECCAK256 or Instruction.EXP or Instruction.MCOPY or Instruction.EXTCODECOPY
            or Instruction.LOG0 or Instruction.LOG1 or Instruction.LOG2 or Instruction.LOG3 or Instruction.LOG4
            or Instruction.CREATE or Instruction.CREATE2 or Instruction.SELFDESTRUCT
            or Instruction.BALANCE or Instruction.EXTCODESIZE or Instruction.EXTCODEHASH or Instruction.SLOAD or Instruction.SSTORE or Instruction.STATICCALL or Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL;
    }

    /// <inheritdoc/>
    public void ReportOperationStart(ulong gasCost, int stackHead, int stackInputs, int stackGrowth)
    {
        _log.gasCost = gasCost;
        if (stackHead < stackInputs)
            _log.error = $"stack underflow ({stackHead} <=> {stackInputs})";
        else if (stackGrowth > 0 && stackHead >= EvmStack.MaxStackSize - stackGrowth)
            _log.error = $"stack limit reached {stackHead} ({EvmStack.MaxStackSize - 1 - stackGrowth})";
        else if (_log.gas < gasCost)
            _log.error = "out of gas";
        _failedBeforeExecution = _log.error is not null;
    }

    /// <inheritdoc/>
    public void ReportOperationGasCost(ulong gasCost) => _log.gasCost = gasCost;

    /// <inheritdoc/>
    public void ReportOperationReady(ulong gasCost, string? error)
    {
        if (!_pendingStep)
            return;

        _log.gasCost = gasCost;
        _log.error = error ?? (_log.gas < gasCost ? "out of gas" : null);
        _failedBeforeExecution = _log.error is not null;
        InvokeStep();
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (!_pendingStep)
            _log.gasCost ??= _log.gas - gas;
        if (_functions.HasFlag(TracerFunctions.postStep))
        {
            Engine.CurrentEngine = _engine;
            _tracer.postStep(_log, _db);
        }
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        if (error is EvmExceptionType.NotEnoughBalance or EvmExceptionType.CallDepthExceeded && _log.op?.Value is
            Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL)
            return;
        if (_failedBeforeExecution)
            return;
        if (error == EvmExceptionType.BadInstruction)
        {
            _log.gasCost = 0;
            _log.error = $"invalid opcode: {_log.op?.toString()}";
        }
        else
        {
            _log.error = error.GetEvmExceptionDescription();
        }
        if (_pendingStep)
        {
            InvokeStep();
            _failedBeforeExecution = true;
            return;
        }
        if (error == EvmExceptionType.AccessViolation && _log.op?.Value == Instruction.RETURNDATACOPY)
        {
            UInt256 sourceOffset = _operationStack.PeekUInt256(1);
            if (sourceOffset.IsUint64)
            {
                // Geth reuses the popped source-offset slot for the end offset before its bounds check.
                UInt256.AddOverflow(sourceOffset, _operationStack.PeekUInt256(2), out UInt256 endOffset);
                byte[] faultStack = _operationStack.ToRawBytes();
                endOffset.ToBigEndian(faultStack.AsSpan((_operationStack.Count - 2) * EvmStack.WordSize, EvmStack.WordSize));
                _log.stack = new Log.Stack(new TraceStack(faultStack));
            }
        }
        Engine.CurrentEngine = _engine;
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

    /// <inheritdoc/>
    public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address? to,
        ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false)
    {
        ReportAction(gas, value, from, to!, input, callType, isPrecompileCall);
        base.ReportActionError(error);
        InvokeExit(gasLeft, ReadOnlyMemory<byte>.Empty, error.GetEvmExceptionDescription());
    }

    /// <inheritdoc/>
    public override void ReportActionRemainingGas(ulong gas) => _actionRemainingGas = gas;

    /// <inheritdoc/>
    public override void ReportActionErrorDetails(string error) => _actionErrorDetails = error;

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        // A rejected CREATE never started a child opcode; its diagnostic checkpoint may refund all gas.
        ulong remainingGas = _actionErrorDetails is not null && _log.depth != _depth + 1 ? _actionRemainingGas : 0;
        InvokeExit(remainingGas, Array.Empty<byte>(), _actionErrorDetails ?? (_log.depth == _depth + 1 && _log.error is not null
            ? _log.error : evmExceptionType.GetEvmExceptionDescription()));
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
            Engine.CurrentEngine = _engine;
            _tracer.exit(_result);
        }

        if (_depth == 0) _rootError = error;
        _actionErrorDetails = null;
        _actionRemainingGas = 0;
        _depth--;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        _ctx.gasUsed = gasSpent.SpentGas;
        _ctx.Output = output;
        _ctx.error = _rootError ?? error;
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
        _operationStack = stack;
        _log.stack = new Log.Stack(stack);

        if (!_pendingStep || _failedBeforeExecution)
            InvokeStep();
    }

    private void InvokeStep()
    {
        _pendingStep = false;
        if (_functions.HasFlag(TracerFunctions.step))
        {
            Engine.CurrentEngine = _engine;
            _tracer.step(_log, _db);
        }
    }

    /// <inheritdoc/>
    public override void ReportStorageRefund(long refund) => _log.refund = CurrentRefund + refund;

    public override void ReportRefund(long refund)
    {
        if (_depth < 0) return;
        base.ReportRefund(refund);
        _log.refund = CurrentRefund;
    }

    private static TracerFunctions GetAvailableFunctions(object tracer)
    {
        bool HasFunction(string name) => tracer is ScriptObject script
            && script.GetProperty(name) is IJavaScriptObject { Kind: JavaScriptObjectKind.Function };

        if (!HasFunction("result"))
            throw new ArgumentException("trace object must expose a function result()");
        if (!HasFunction("fault"))
            throw new ArgumentException("trace object must expose a function fault()");

        TracerFunctions result = TracerFunctions.result | TracerFunctions.fault;
        foreach (TracerFunctions function in FastEnum.GetValues<TracerFunctions>())
        {
            if (function > TracerFunctions.result && HasFunction(FastEnum.GetName(function)))
                result |= function;
        }

        if (result.HasFlag(TracerFunctions.enter) != result.HasFlag(TracerFunctions.exit))
            throw new ArgumentException("trace object must expose either both or none of enter() and exit()");

        return result;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            base.Dispose();
            _ctsRegistration.Dispose();
            _deadline?.Dispose();
        }
        finally
        {
            try
            {
                ((object)_tracer as IDisposable)?.Dispose();
            }
            finally
            {
                _engine.Dispose();
            }
        }
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
