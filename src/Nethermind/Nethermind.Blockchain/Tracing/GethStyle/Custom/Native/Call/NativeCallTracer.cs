// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

// The callTracer tracks all the call frames executed during a transaction, including depth 0.
// The result will be a nested list of call frames, resembling how the EVM works.
// They form a tree with the top-level call at root and sub-calls as children of the higher levels.
//
// TracerConfig options:
// onlyTopCall (default = false): Only the main (top-level) call will be processed to avoid any extra processing if only the main call info is required.
// withLog (default = false): Logs emitted during each call will also be collected and included in the result.
//
// An EIP-8141 frame transaction runs every frame as its own top-level invocation, so it has no single
// root call. Its trace is rooted in one synthetic transaction frame whose children are its frames, so
// that `calls[i]` is `tx.Frames[i]` and the result stays a single CallFrame for consumers that walk
// `calls`. Two deliberate divergences follow: onlyTopCall still returns that root with its frames as
// children, and a frame that never entered the VM is rendered from the transaction's frame list. Every
// frame's gas and gasUsed are its declared limit and its receipt's spend, so siblings read alike.
public sealed class NativeCallTracer : GethLikeNativeTxTracer, IFrameTxReceiptTracer
{
    public const string CallTracer = "callTracer";

    /// <summary>Error reported for a frame an unrolled atomic batch or a failed assertion never ran.</summary>
    private const string SkippedFrameError = "frame skipped";

    private readonly ulong _gasLimit;
    private readonly Hash256? _txHash;
    private readonly bool _isEip8037Enabled;
    private readonly bool _isFrameTx;
    private readonly Address? _sender;
    private readonly TxFrame[]? _frames;
    private readonly NativeCallTracerConfig _config;
    private readonly ArrayPoolList<NativeCallTracerCallFrame> _callStack = new(1024);
    private readonly CompositeDisposable _disposables = [];

    private EvmExceptionType? _error;
    private ulong _remainingGas;
    private bool _resultBuilt = false;
    private bool _framesCollapsed = false;
    private NativeCallTracerCallFrame?[]? _frameRoots;
    private EvmExceptionType?[]? _frameErrors;
    private TxFrameReceipt[]? _frameReceipts;
    private int _rootsClaimed;

    public NativeCallTracer(
        Transaction? tx,
        IReleaseSpec spec,
        GethTraceOptions options) : base(options)
    {
        IsTracingActions = true;
        _gasLimit = tx!.GasLimit;
        _txHash = tx.Hash;
        _isEip8037Enabled = spec.IsEip8037Enabled;
        _isFrameTx = tx.SupportsFrames;
        _sender = tx.SenderAddress;
        _frames = _isFrameTx ? tx.Frames : null;

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

        CollapseFrameRoots();

        Debug.Assert(_callStack.Count <= 1, $"Unexpected frames on call stack, expected at most one master frame, found {_callStack.Count} frames.");

        if (_callStack.Count is not 0)
        {
            NativeCallTracerCallFrame firstCallFrame = _callStack[0];
            _callStack.RemoveAt(0);
            _disposables.Add(firstCallFrame);

            result.TxHash = _txHash;
            result.CustomTracerResult = new GethLikeCustomTrace { Value = firstCallFrame };
        }

        result.TxHash = _txHash;
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

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
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
            // A frame transaction's top-level invocations each carry their own limit; the whole
            // transaction's belongs to the synthetic root CollapseFrameRoots builds.
            Gas = Depth == 0 && !_isFrameTx ? _gasLimit : gas,
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

        // A frame transaction running entirely through default code (an EOA sender's codeless
        // SENDER transfer) emits a log without any ReportAction, so the call stack can be empty.
        if (_callStack.Count == 0)
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

    public override void ReportOperationRemainingGas(ulong gas)
    {
        base.ReportOperationRemainingGas(gas);
        _remainingGas = gas > 0 ? gas : 0;
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        OnExit(gas, deployedCode);
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
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

    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
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
            NativeCallTracerCallFrame callFrame = new()
            {
                Type = Instruction.SELFDESTRUCT,
                From = address,
                To = refundAddress,
                Value = balance
            };
            _callStack[^1].Calls.Add(callFrame);
        }
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        CollapseFrameRoots();
        if (_callStack.Count == 0) return;
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent.SpentGas;
        firstCallFrame.Output = new ArrayPoolList<byte>(output);
        ApplyTwoDimensionalGas(firstCallFrame, in gasSpent);

        if (_config.WithLog)
        {
            ClearFailedLogs(firstCallFrame, parentFailed: false);
        }
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);

        CollapseFrameRoots();
        if (_callStack.Count == 0) return;
        NativeCallTracerCallFrame firstCallFrame = _callStack[0];
        firstCallFrame.GasUsed = gasSpent.SpentGas;
        ApplyTwoDimensionalGas(firstCallFrame, in gasSpent);
        if (output is not null)
            firstCallFrame.Output = new ArrayPoolList<byte>(output);

        if (_isFrameTx)
        {
            // A frame transaction fails at the transaction level — a POST_TX frame failing before dispatch
            // reports no EVM error at all — so the reason the processor gives is the root's.
            firstCallFrame.Error = error ?? EvmExceptionType.Revert.GetEvmExceptionDescription();
        }
        else if (_error is not null)
        {
            EvmExceptionType errorType = _error.Value;
            MarkFrameFailed(firstCallFrame, errorType);
            if (errorType == EvmExceptionType.Revert && error is not TransactionSubstate.Revert)
            {
                firstCallFrame.RevertReason = ValidateRevertReason(error);
            }
        }

        if (_config.WithLog)
        {
            if (_isFrameTx)
            {
                // The synthetic root's error is transaction-level, so it must not propagate: the committed
                // frames keep their logs as the receipt does, having been cleared against it already.
                foreach (NativeCallTracerCallFrame frameCallFrame in firstCallFrame.Calls.AsSpan())
                {
                    ClearFailedLogs(frameCallFrame, parentFailed: false);
                }
            }
            else
            {
                ClearFailedLogs(firstCallFrame, parentFailed: true);
            }
        }
    }

    /// <inheritdoc/>
    public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts)
    {
        // The validation-prefix simulation reports an empty set, which pins no frame's outcome and so
        // must not collapse the result either.
        if (frameReceipts.Length == 0) return;

        _frameReceipts = frameReceipts;
        CollapseFrameRoots();
    }

    /// <inheritdoc/>
    public void ReportFrameEnd(int frameIndex, EvmExceptionType? error)
    {
        if (_frames is null || (uint)frameIndex >= (uint)_frames.Length) return;

        _frameRoots ??= new NativeCallTracerCallFrame?[_frames.Length];
        _frameErrors ??= new EvmExceptionType?[_frames.Length];
        _frameErrors[frameIndex] = error;

        // Only a frame that entered the VM pushed a root, and it pushed exactly one.
        if (_callStack.Count > _rootsClaimed)
        {
            _frameRoots[frameIndex] = _callStack[_rootsClaimed++];
        }
    }

    /// <summary>Roots an EIP-8141 frame transaction's trace in one synthetic transaction frame whose
    /// children are its frames, in frame order.</summary>
    /// <remarks>A frame transaction has no single root call: each frame is its own top-level invocation, so
    /// without this every frame after the first is dropped from the result. Children come from the per-frame
    /// receipts rather than from the VM roots, because a frame that fails before dispatch — and a
    /// <c>VERIFY</c> frame running the default code — never enters the VM, and because only the receipt says
    /// which frames kept their logs. The synthetic root is the only frame carrying the transaction-wide gas
    /// figures. Idempotent, because both the receipt callbacks and <see cref="BuildResult"/> reach it.</remarks>
    private void CollapseFrameRoots()
    {
        if (!_isFrameTx || _framesCollapsed) return;
        _framesCollapsed = true;

        NativeCallTracerCallFrame txCallFrame = new()
        {
            Type = Instruction.CALL,
            From = _sender,
            To = Eip8141Constants.EntryPointAddress,
            Gas = _gasLimit,
            Value = UInt256.Zero
        };

        int rootsTaken = 0;
        if (_frameReceipts is not null && _frames is not null && _frameRoots is not null)
        {
            rootsTaken = _rootsClaimed;
            int frameCount = Math.Min(_frameReceipts.Length, _frames.Length);
            for (int i = 0; i < frameCount; i++)
            {
                TxFrameReceipt frameReceipt = _frameReceipts[i];
                NativeCallTracerCallFrame frameCallFrame = _frameRoots[i] ?? BuildUndispatchedFrame(i, frameReceipt);

                // Uniform across dispatched and undispatched frames, where the VM's own figures would be the
                // execution dimension net of the pre-dispatch charges.
                frameCallFrame.Gas = _frames[i].GasLimit;
                frameCallFrame.GasUsed = frameReceipt.GasUsed;

                if (_config.WithLog && frameReceipt.Logs.Length == 0)
                {
                    // The receipt is what the frame committed: an unrolled atomic batch and a POST_TX
                    // rollback both keep the frame's success status while dropping its logs.
                    ClearFailedLogs(frameCallFrame, parentFailed: true);
                }

                txCallFrame.Calls.Add(frameCallFrame);
            }
        }

        // Everything left over, so no root is dropped on a path that reports no receipts.
        for (int i = rootsTaken; i < _callStack.Count; i++)
        {
            txCallFrame.Calls.Add(_callStack[i]);
        }

        _callStack.Clear();
        _callStack.Add(txCallFrame);
    }

    /// <summary>Renders a frame that never entered the VM from the transaction's own frame list.</summary>
    private NativeCallTracerCallFrame BuildUndispatchedFrame(int frameIndex, TxFrameReceipt frameReceipt)
    {
        TxFrame frame = _frames![frameIndex];
        bool isStatic = frame.Mode is TxFrame.ModeVerify or TxFrame.ModePostTx;
        return new NativeCallTracerCallFrame
        {
            Type = isStatic ? Instruction.STATICCALL : Instruction.CALL,
            From = frame.Mode == TxFrame.ModeSender ? _sender : Eip8141Constants.EntryPointAddress,
            To = frame.Target ?? _sender,
            Value = isStatic ? null : frame.Value,
            Input = frame.Data.Span.ToPooledList(),
            Error = UndispatchedFrameError(frameIndex, frameReceipt)
        };
    }

    private string? UndispatchedFrameError(int frameIndex, TxFrameReceipt frameReceipt) => frameReceipt.Status switch
    {
        TxFrameReceipt.StatusSuccess => null,
        TxFrameReceipt.StatusSkipped => SkippedFrameError,
        _ => (_frameErrors?[frameIndex] ?? EvmExceptionType.Revert).GetEvmExceptionDescription()
    };

    private void ApplyTwoDimensionalGas(NativeCallTracerCallFrame firstCallFrame, in GasConsumed gasSpent)
    {
        if (!_isEip8037Enabled) return;

        firstCallFrame.Eip8037Gas = new TwoDimensionalGas(gasSpent.EffectiveBlockGas, gasSpent.BlockStateGas, gasSpent.GasRefund);
    }

    private void OnExit(ulong gas, ReadOnlyMemory<byte>? output, EvmExceptionType? error = null)
    {
        if (Depth == 0)
        {
            // Only a frame transaction reaches this with more frames to come; every other transaction's
            // root is finished by MarkAsSuccess/MarkAsFailed with the transaction-wide figures.
            if (_isFrameTx && _callStack.Count > 0)
            {
                NativeCallTracerCallFrame frameRoot = _callStack[^1];
                frameRoot.GasUsed = frameRoot.Gas - gas;
                ProcessOutput(frameRoot, output, error);
            }

            return;
        }

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

    /// <summary>Records an EVM halt on a call frame, for the root frame and the nested ones alike.</summary>
    /// <remarks>
    /// A CREATE or CREATE2 frame that halted deployed no contract, so its <c>to</c> is dropped —
    /// the execution-apis <c>CallFrame</c> schema requires it to be omitted there.
    /// </remarks>
    private static void MarkFrameFailed(NativeCallTracerCallFrame callFrame, EvmExceptionType error)
    {
        callFrame.Error = error.GetEvmExceptionDescription();
        if (callFrame.Type is Instruction.CREATE or Instruction.CREATE2)
        {
            callFrame.To = null;
        }
    }

    private static void ProcessOutput(NativeCallTracerCallFrame callFrame, ReadOnlyMemory<byte>? output, EvmExceptionType? error)
    {
        if (error is not null)
        {
            MarkFrameFailed(callFrame, error.Value);

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
            callFrame.Logs?.Dispose();
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
