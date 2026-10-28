// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);

    private readonly dynamic _tracer;
    private readonly Log _log = new();
    private readonly Engine _engine;
    private readonly Db _db;
    private readonly CallFrame _frame = new();
    private readonly FrameResult _result = new();
    private readonly CancellationTokenSource? _cts;
    private readonly GethTraceDeadline? _deadline;
    private readonly bool _ownsDeadline;
    private readonly CancellationToken _external;
    private readonly CancellationTokenRegistration _ctsRegistration;
    private bool _disposed;
    private readonly bool _captureErrors;
    private readonly Dictionary<TracerFunctions, ScriptObject>? _callbacks;
    private string? _traceError;
    private bool _failedBeforeExecution;
    private bool _pendingStep;
    private TraceStack _operationStack;
    private Stack<ulong>? _frameGas;
    private Stack<Log.Contract>? _contracts;
    private int _depth = -1;

    // Context is updated only of first ReportAction call.
    private readonly Context _ctx;
    private readonly TracerFunctions _functions;

    /// <summary>Creates a JavaScript tracer using the supplied execution context and engine.</summary>
    /// <remarks>SELFDESTRUCT refunds are credited at transaction finalization, avoiding a second credit at the opcode boundary.</remarks>
    public GethLikeJavaScriptTxTracer(
        Engine engine,
        Db db,
        Context ctx,
        GethTraceOptions options) : base(options, destroyRefund: 0)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;
        // The script sees storage through db and has no return data on log, so the per-operation hooks would only
        // build values for empty callbacks.
        IsTracingOpLevelStorage = false;
        IsTracingReturnData = false;

        _engine = engine;
        _db = db;
        _ctx = ctx;
        _captureErrors = options.CaptureJavaScriptErrors;
        if (_captureErrors)
        {
            _callbacks = [];
            _log.stack.InputErrorEngine = engine;
            _log.memory.InputErrorEngine = engine;
            engine.PrepareNullThrowCapture();

        }

        Engine.CurrentEngine = _engine;
        _tracer = engine.CreateTracer(options.Tracer);
        try
        {
            try
            {
                _functions = _captureErrors
                    ? CaptureFunctions((object)_tracer)
                    : GetAvailableFunctions(((IDictionary<string, object>)_tracer).Keys);
            }
            catch (ArgumentException exception) when (_captureErrors)
            {
                throw new JavaScriptTraceFailure(exception);
            }
            if (_functions.HasFlag(TracerFunctions.setup))
            {
                Invoke(TracerFunctions.setup, options.TracerConfig?.ToString() ?? "{}");
            }

            TimeSpan timeout;
            try
            {
                timeout = options.ResolveTimeout() ?? GethTraceDeadline.DefaultTimeout;
            }
            catch (TraceDurationFormatException exception) when (_captureErrors)
            {
                throw new JavaScriptTraceFailure(exception);
            }
            if (options.EnforceExecutionDeadline)
            {
                _ownsDeadline = options.DeadlineScope?.Current is null;
                _external = options.DeadlineScope?.External ?? default;
                _deadline = _ownsDeadline ? new(_external, options.DeadlineClock) : options.DeadlineScope!.Current;
                _ctsRegistration = (_deadline?.Token ?? _external).Register(static e => ((Engine)e!).Interrupt(), engine);
                if (_ownsDeadline) _deadline!.Start(timeout);
            }
            else
            {
                if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
                    throw new ArgumentOutOfRangeException(nameof(options), timeout, $"Tracer timeout must be between 1ns and {MaxTimeout.TotalMinutes}m.");
                _cts = new CancellationTokenSource(timeout);
                _ctsRegistration = _cts.Token.Register(static e => ((Engine)e!).Interrupt(), engine);
            }
        }
        catch
        {
            _ctsRegistration.Dispose();
            if (_ownsDeadline) _deadline?.Dispose();
            _cts?.Dispose();
            DisposeCallbacks();
            throw;
        }
    }

    public override GethLikeTxTrace BuildResult()
    {
        CheckDeadline();
        GethLikeTxTrace result = base.BuildResult();

        result.TxHash = _ctx.TxHash;
        if (!_captureErrors)
        {
            result.CustomTracerResult = new GethLikeCustomTrace { Value = MaterializeResult(Invoke(TracerFunctions.result)) };
            Dispose();
            return result;
        }
        try
        {
            object? value = Invoke(TracerFunctions.result);
            if (_traceError is null)
                result.CustomTracerResult = new GethLikeCustomTrace { Value = MaterializeResult(value) };
            CheckDeadline();
        }
        catch (Exception exception) when (IsDeadlineInterruption(exception))
        {
            _external.ThrowIfCancellationRequested();
            throw new GethTraceDeadlineException();
        }
        catch (Exception exception) when (JavaScriptTraceFailure.IsRecoverable(exception))
        {
            CheckDeadline();
            _traceError = exception.Message;
        }
        finally
        {
            Dispose();
        }
        result.TraceError = _traceError;

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
            return new RenderedJson(TypeInfoJsonSerializer.SerializeToUtf8Bytes(scriptResult, EthereumJsonSerializer.JsonOptions));
        }
        finally
        {
            ForcedNumberConversion.Value = previousConversion;
        }
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
                Invoke(TracerFunctions.enter);
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
        CheckDeadline();
        _log.refund = CurrentRefund;
        _log.pc = pc;
        _log.op = Log.Opcode.Of(opcode);
        _log.gas = gas;
        _log.depth = env.GetGethTraceDepth();
        _log.error = null;
        _log.gasCost = null;
        _failedBeforeExecution = false;
        _pendingStep = opcode is Instruction.RETURNDATACOPY or Instruction.RETURN or Instruction.REVERT
            or Instruction.MLOAD or Instruction.MSTORE or Instruction.MSTORE8 or Instruction.CALLDATACOPY or Instruction.CODECOPY
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
            Invoke(TracerFunctions.postStep);
        }
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        if (error == EvmExceptionType.NotEnoughBalance && _log.op?.Value is
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
                _log.stack.Items = new TraceStack(faultStack);
            }
        }
        Invoke(TracerFunctions.fault);
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
            Invoke(TracerFunctions.exit);
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
        _operationStack = stack;
        _log.stack.Items = stack;

        if (!_pendingStep || _failedBeforeExecution)
            InvokeStep();
    }

    private void InvokeStep()
    {
        _pendingStep = false;
        if (_functions.HasFlag(TracerFunctions.step))
        {
            Invoke(TracerFunctions.step);
        }
    }

    /// <inheritdoc/>
    public override void ReportStorageRefund(long refund) => _log.refund = CurrentRefund + refund;

    public override void ReportRefund(long refund)
    {
        base.ReportRefund(refund);
        _log.refund = CurrentRefund;
    }

    private TracerFunctions CaptureFunctions(object tracer)
    {
        TracerFunctions functions = Capture(TracerFunctions.result, required: true) | Capture(TracerFunctions.fault, required: true);
        functions |= Capture(TracerFunctions.step) | Capture(TracerFunctions.enter) | Capture(TracerFunctions.exit);
        if (functions.HasFlag(TracerFunctions.enter) != functions.HasFlag(TracerFunctions.exit))
            throw new ArgumentException("trace object must expose either both or none of enter() and exit()");

        return functions | Capture(TracerFunctions.postStep) | Capture(TracerFunctions.setup);

        TracerFunctions Capture(TracerFunctions function, bool required = false)
        {
            string name = FastEnum.GetName(function);
            if (tracer is ScriptObject script && script.GetProperty(name) is ScriptObject callback
                && callback is IJavaScriptObject { Kind: JavaScriptObjectKind.Function })
            {
                _callbacks!.Add(function, callback);
                return function;
            }
            if (required) throw new ArgumentException($"trace object must expose required function {name}");
            return TracerFunctions.none;
        }
    }

    private void DisposeCallbacks()
    {
        if (_callbacks is null) return;
        foreach (ScriptObject callback in _callbacks.Values) callback.Dispose();
        _callbacks.Clear();
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
        try
        {
            base.Dispose();
            _ctsRegistration.Dispose();
            _cts?.Dispose();
            if (_ownsDeadline) _deadline?.Dispose();
        }
        finally
        {
            try
            {
                DisposeCallbacks();
                ((object)_tracer as IDisposable)?.Dispose();
            }
            finally
            {
                _engine.Dispose();
            }
        }
    }

    private void CheckDeadline()
    {
        _external.ThrowIfCancellationRequested();
        if (_deadline?.Expired == true) throw new GethTraceDeadlineException();
    }

    private bool IsDeadlineInterruption(Exception exception)
    {
        if (_deadline?.Expired != true && !_external.IsCancellationRequested) return false;
        while (true)
        {
            switch (exception)
            {
                case ScriptInterruptedException { IsFatal: false, InnerException: null }:
                    return true;
                case ScriptInterruptedException { IsFatal: false, InnerException: { } inner }:
                    exception = inner;
                    break;
                case ScriptEngineException { IsFatal: false, InnerException: { } inner }:
                    exception = inner;
                    break;
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    break;
                default:
                    return false;
            }
        }
    }

    private object? Invoke(TracerFunctions function, string? config = null)
    {
        CheckDeadline();
        // A failed JS hook must not abort canonical execution or corrupt later transactions' prestate.
        if (_traceError is not null) return null;
        Engine.CurrentEngine = _engine;
        bool observedNullThrow = false;
        try
        {
            if (_captureErrors)
            {
                (object? First, object? Second, bool HasSecond) arguments = function switch
                {
                    TracerFunctions.setup => (config, null, false),
                    TracerFunctions.result => (_ctx, _db, true),
                    TracerFunctions.enter => (_frame, null, false),
                    TracerFunctions.exit => (_result, null, false),
                    TracerFunctions.step or TracerFunctions.postStep or TracerFunctions.fault => (_log, _db, true),
                    _ => throw new ArgumentOutOfRangeException(nameof(function))
                };
                object? value = _engine.InvokeCapturingNull((object)_tracer, _callbacks![function], arguments.First, arguments.Second, arguments.HasSecond, out observedNullThrow);
                _engine.ThrowIfInputFailed();
                CheckDeadline();
                return value;
            }
            return function switch
            {
                TracerFunctions.setup => _tracer.setup(config),
                TracerFunctions.result => _tracer.result(_ctx, _db),
                TracerFunctions.enter => _tracer.enter(_frame),
                TracerFunctions.exit => _tracer.exit(_result),
                TracerFunctions.step => _tracer.step(_log, _db),
                TracerFunctions.postStep => _tracer.postStep(_log, _db),
                TracerFunctions.fault => _tracer.fault(_log, _db),
                _ => throw new ArgumentOutOfRangeException(nameof(function))
            };
        }
        catch (Exception exception) when (IsDeadlineInterruption(exception))
        {
            _external.ThrowIfCancellationRequested();
            throw new GethTraceDeadlineException();
        }
        catch (Exception exception) when (_captureErrors
            && (_engine.TryGetInputError(exception, out string? inputError, observedNullThrow)
                || JavaScriptTraceFailure.IsRecoverable(exception, observedNullThrow)))
        {
            CheckDeadline();
            _engine.ThrowIfInterrupted();
            if (function == TracerFunctions.setup)
                throw new JavaScriptTraceFailure(exception, inputError ?? exception.Message);
            _traceError = inputError ?? exception.Message;
            return null;
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
