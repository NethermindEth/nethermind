// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
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
    private readonly Hash256? _txHash;
    private readonly NativeCallTracerConfig _config;
    private readonly ArrayPoolList<NativeCallTracerCallFrame> _callStack = new(1024);
    private readonly CompositeDisposable _disposables = new();

    private EvmExceptionType? _error;
    private long _remainingGas;
    private bool _resultBuilt = false;

    public NativeCallTracer(
        Transaction? tx,
        GethTraceOptions options) : base(options)
    {
        IsTracingActions = true;
        _gasLimit = tx!.GasLimit;
        _txHash = tx.Hash;

        _config = options.TracerConfig?.Deserialize<NativeCallTracerConfig>(EthereumJsonSerializer.JsonOptions) ?? new NativeCallTracerConfig();

        if (_config.WithLog)
        {
            IsTracingLogs = true;
        }
    }

    protected override GethLikeTxTrace CreateTrace() => new(_disposables);

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];

        Debug.Assert(_callStack.Count == 1, $"Unexpected frames on call stack, expected only master frame, found {_callStack.Count} frames.");

        _callStack.RemoveAt(0);
        _disposables.Add(firstCallFrame);

        result.TxHash = _txHash;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = firstCallFrame };

        _resultBuilt = true;

        return result;
    }

    public override void Dispose()
    {
        base.Dispose();
        for (int i = _resultBuilt ? 1 : 0; i < _callStack.Count; i++)
        {
            _callStack[i].Dispose();
        }

        _callStack.Dispose();
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (_config.OnlyTopCall && Depth > 0)
            return;

        Instruction callOpcode = callType.ToInstruction();
        NativeCallTracerCallFrame callFrame = new()
        {
            Type = callOpcode,
            From = from,
            To = to,
            Gas = Depth == 0 ? _gasLimit : gas,
            Value = callOpcode == Instruction.STATICCALL ? null : value,
            Input = input.Span.ToPooledList()
        };
        _callStack.Add(callFrame);
    }

    public override void ReportLog(LogEntry log)
    {
        base.ReportLog(log);

        if (_config.OnlyTopCall && Depth > 0)
            return;

        NativeCallTracerCallFrame callFrame = _callStack[^1];

        NativeCallTracerLogEntry callLog = new(
            log.Address,
            log.Data,
            log.Topics,
            (ulong)callFrame.Calls.Count);

        callFrame.Logs ??= new ArrayPoolList<NativeCallTracerLogEntry>(8);
        callFrame.Logs.Add(callLog);
    }

    public override void ReportOperationRemainingGas(long gas)
    {
        base.ReportOperationRemainingGas(gas);
        _remainingGas = gas > 0 ? gas : 0;
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
        OnExit(_remainingGas, null, _error);
        base.ReportActionError(evmExceptionType);
    }

    public override void ReportActionRevert(long gas, ReadOnlyMemory<byte> output)
    {
        _error = EvmExceptionType.Revert;
        OnExit(gas, output, _error);
        base.ReportActionRevert(gas, output);
    }

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        base.ReportSelfDestruct(address, balance, refundAddress);
        if (!_config.OnlyTopCall && _callStack.Count > 0)
        {
            NativeCallTracerCallFrame callFrame = new NativeCallTracerCallFrame
            {
                Type = Instruction.SELFDESTRUCT,
                From = address,
                To = refundAddress,
                Value = balance
            };
            _callStack[^1].Calls.Add(callFrame);
        }
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent.SpentGas;
        firstCallFrame.Output = new ArrayPoolList<byte>(output);
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent.SpentGas;
        if (output is not null)
            firstCallFrame.Output = new ArrayPoolList<byte>(output);

        EvmExceptionType errorType = _error!.Value;
        firstCallFrame.Error = errorType.GetEvmExceptionDescription();
        if (errorType == EvmExceptionType.Revert && error is not TransactionSubstate.Revert)
        {
            firstCallFrame.RevertReason = ValidateRevertReason(error);
        }

        if (_config.WithLog)
        {
            ClearFailedLogs(firstCallFrame, false);
        }
    }

    private void OnExit(long gas, ReadOnlyMemory<byte>? output, EvmExceptionType? error = null)
    {
        if (!_config.OnlyTopCall && Depth > 0)
        {
            NativeCallTracerCallFrame callFrame = _callStack[^1];

            int size = _callStack.Count;
            if (size > 1)
            {
                _callStack.RemoveAt(size - 1);
                callFrame.GasUsed = callFrame.Gas - gas;

                ProcessOutput(callFrame, output, error);

                _callStack[^1].Calls.Add(callFrame);
            }
        }
    }

    private static void ProcessOutput(NativeCallTracerCallFrame callFrame, ReadOnlyMemory<byte>? output, EvmExceptionType? error)
    {
        if (error is not null)
        {
            callFrame.Error = error.Value.GetEvmExceptionDescription();
            if (callFrame.Type is Instruction.CREATE or Instruction.CREATE2)
            {
                callFrame.To = null;
            }

            if (error == EvmExceptionType.Revert && output?.Length != 0)
            {
                ArrayPoolList<byte> outputList = output?.Span.ToPooledList();
                callFrame.Output = outputList;

                if (outputList?.Count >= 4)
                {
                    ProcessRevertReason(callFrame, output.Value);
                }
            }
        }
        else
        {
            callFrame.Output = output?.Span.ToPooledList();
        }
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

        foreach (NativeCallTracerCallFrame childCallFrame in callFrame.Calls.AsSpan())
        {
            ClearFailedLogs(childCallFrame, failed);
        }
    }

    private static string? ValidateRevertReason(string? errorMessage) =>
        errorMessage?.StartsWith("0x") == false ? errorMessage : null;
}
