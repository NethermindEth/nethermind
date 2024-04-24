// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;

// The callTracer tracks all the call frames executed during a transaction, including depth 0.
// The result will be a nested list of call frames, resembling how the EVM works.
// They form a tree with the top-level call at root and sub-calls as children of the higher levels.
//
// TracerConfig options:
// onlyTopCall (default = false): Only the main (top-level) call will be processed to avoid any extra processing if only the main call info is required.
// withLog (default = false): Logs emitted during each call will also be collected and included in the result.
public sealed class NativeCallTracer : GethLikeNativeTxTracer
{
    public const string CallTracer = "callTracer";

    private readonly long _gasLimit;
    private readonly bool _onlyTopCall;
    private readonly bool _withLog;
    private readonly List<NativeCallTracerCallFrame> _callStack;

    private EvmExceptionType? _error;

    public NativeCallTracer(
        Transaction? tx,
        GethTraceOptions options) : base(options)
    {
        IsTracingActions = true;

        _callStack = [];
        _gasLimit = tx!.GasLimit;

        NativeCallTracerConfig config = options.TracerConfig?.Deserialize<NativeCallTracerConfig>(EthereumJsonSerializer.JsonOptions) ?? new NativeCallTracerConfig();
        _onlyTopCall = config.OnlyTopCall;
        _withLog = config.WithLog;

        if (_withLog)
        {
            IsTracingOpLevelLogs = true;
        }
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        result.CustomTracerResult = new GethLikeCustomTrace { Value = firstCallFrame };
        return result;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (_onlyTopCall && Depth > 0)
            return;

        Instruction callOpcode = callType.ToInstruction();
        NativeCallTracerCallFrame callFrame = new NativeCallTracerCallFrame
        {
            Type = callOpcode,
            From = from,
            To = to,
            Gas = Depth == 0 ? _gasLimit : gas,
            Value = callOpcode == Instruction.STATICCALL ? null : value,
            Input = input.ToArray(),
            Calls = []
        };
        _callStack.Add(callFrame);
    }

    public override void ReportOperationLog(LogEntry log)
    {
        base.ReportOperationLog(log);

        if (_onlyTopCall && Depth > 0)
            return;

        NativeCallTracerCallFrame callFrame = _callStack[^1];

        NativeCallTracerLogEntry callLog = new NativeCallTracerLogEntry(
            log.LoggersAddress,
            log.Data,
            log.Topics,
            (ulong)callFrame.Calls.Count);

        callFrame.Logs ??= [];
        callFrame.Logs.Add(callLog);
    }

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        OnExit(gas, deployedCode);
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
    }

    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        OnExit(gas, output);
        base.ReportActionEnd(gas, output);
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        _error = evmExceptionType;
        base.ReportActionError(evmExceptionType);
    }

    public override void ReportActionRevert(long gas, ReadOnlyMemory<byte> output)
    {
        // If a specific EvmExceptionType is set then show that instead of the default revert error for the call frame the error occurred in.
        // For each parent call, show the revert error.
        OnExit(gas, output, _error ?? EvmExceptionType.Revert);
        _error = EvmExceptionType.Revert;
        base.ReportActionRevert(gas, output);
    }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent;
        firstCallFrame.Output = output;
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent;
        firstCallFrame.Output = output;

        EvmExceptionType errorType = _error!.Value;
        firstCallFrame.Error = errorType.GetEvmExceptionDescription();
        int revertedPrefixLength = TransactionSubstate.RevertedErrorMessagePrefix.Length;
        if (errorType == EvmExceptionType.Revert && error.Length > revertedPrefixLength)
        {
            firstCallFrame.RevertReason = ValidateRevertReason(error[revertedPrefixLength..]);
        }

        if (_withLog)
        {
            ClearFailedLogs(firstCallFrame, false);
        }
    }

    private void OnExit(long gas, ReadOnlyMemory<byte>? output, EvmExceptionType? error = null)
    {
        if (!_onlyTopCall &&  Depth > 0)
        {
            NativeCallTracerCallFrame callFrame = _callStack[^1];

            int size = _callStack.Count;
            if (size <= 1)
                return;

            _callStack.RemoveAt(size - 1);
            callFrame.GasUsed = callFrame.Gas - gas;

            if (output is not null)
                ProcessOutput(callFrame, output.Value, error);

            _callStack[^1].Calls.Add(callFrame);
        }
    }

    private static void ProcessOutput(NativeCallTracerCallFrame callFrame, ReadOnlyMemory<byte> output, EvmExceptionType? error)
    {
        byte[] outputArray = output.ToArray();
        if (error is null)
        {
            callFrame.Output = outputArray;
            return;
        }
        callFrame.Error = error.Value.GetEvmExceptionDescription();
        if (callFrame.Type is Instruction.CREATE or Instruction.CREATE2)
            callFrame.To = null;
        if (error != EvmExceptionType.Revert || outputArray.Length == 0)
            return;

        callFrame.Output = outputArray;
        if (outputArray.Length < 4)
            return;

        ProcessRevertReason(callFrame, output);
    }

    private static void ProcessRevertReason(NativeCallTracerCallFrame callFrame, ReadOnlyMemory<byte> output)
    {
        ReadOnlySpan<byte> span = output.Span;
        string errorMessage;
        try
        {
            errorMessage = TransactionSubstate.GetErrorMessage(span);
        }
        catch
        {
            errorMessage = TransactionSubstate.EncodeErrorMessage(span);
        }
        callFrame.RevertReason = ValidateRevertReason(errorMessage);
    }

    private static void ClearFailedLogs(NativeCallTracerCallFrame callFrame, bool parentFailed)
    {
        bool failed = callFrame.Error is not null || parentFailed;
        if (failed)
        {
            callFrame.Logs = null;
        }
        foreach (NativeCallTracerCallFrame childCallFrame in callFrame.Calls)
        {
            ClearFailedLogs(childCallFrame, failed);
        }
    }

    private static string? ValidateRevertReason(string? errorMessage)
    {
        return errorMessage is not null && !errorMessage.StartsWith("0x") ? errorMessage : null;
    }
}
