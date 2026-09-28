// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public partial class ParityLikeTxTracer : IFrameTxReceiptTracer
{
    private readonly IReleaseSpec? _spec;
    private FrameTxTraceBuilder? _frameTx;

    private FrameTxTraceBuilder? CreateFrameTxTraceBuilder(Transaction? tx) =>
        tx?.Type == TxType.FrameTx && IsTracingActions ? new FrameTxTraceBuilder(this, tx) : null;

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFrameEnd(int frameIndex, EvmExceptionType? error) =>
        _frameTx?.ReportFrameEnd(frameIndex, error);

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) =>
        _frameTx?.ReportFrameTxReceipt(frameReceipts);

    /// <summary>Called as an EIP-8141 frame enters the VM, before its own vmTrace frame opens.</summary>
    private protected virtual void OnEnterFrame(ParityTraceAction frame) => _frameTx!.AddFrameOperation(frame);

    /// <summary>Called once an EIP-8141 frame has left the VM with <paramref name="gasLeft"/> unspent.</summary>
    private protected virtual void OnLeaveFrame(ulong gasLeft) => _frameTx!.CloseFrameOperation(gasLeft);

    /// <summary>Called once the frame at <paramref name="frameIndex"/> completes, whether or not it entered the VM.</summary>
    private protected virtual void OnFrameEnd(int frameIndex) => _frameTx!.PlaceFrameOperation(frameIndex);

    /// <summary>Roots an EIP-8141 frame transaction's trace in one synthetic transaction call whose children are its frames.</summary>
    /// <remarks>
    /// A frame transaction has no single top-level call: each frame is its own top-level invocation. Frame <c>i</c> sits
    /// at trace address <c>[i]</c>; frames that never entered the VM are rebuilt from the transaction once the receipts
    /// are reported, with a frame's gas and gasUsed being its limit and its receipt's spend, as in callTracer. In the
    /// vmTrace, each frame that ran hangs off a synthetic operation of the root, as a callee hangs off its CALL.
    /// </remarks>
    private sealed class FrameTxTraceBuilder(ParityLikeTxTracer tracer, Transaction tx)
    {
        private const string SkippedFrameError = "frame skipped";

        /// <summary>The root's error when a frame failed, which the receipt reports as a failed transaction.</summary>
        private const string FrameFailedError = "frame failed";

        private readonly TxFrame[] _frames = tx.Frames ?? [];
        private ParityTraceAction? _root;
        private ParityTraceAction? _lastFrameAction;
        private ParityVmOperationTrace? _lastFrameOperation;
        private ParityTraceAction?[]? _frameActions;
        private EvmExceptionType?[]? _frameErrors;
        private TxFrameReceipt[]? _frameReceipts;
        private ulong _failedActionGasLeft;
        private bool _framesOrdered;

        public void EnsureRoot()
        {
            if (_root is not null) return;
            _root = CreateRoot();
            tracer.PushAction(_root);
        }

        public void CloseRoot()
        {
            if (_root is not null && tracer._currentAction == _root)
            {
                tracer.PopAction();
            }
        }

        public void MarkSucceeded(in GasConsumed gasSpent)
        {
            EnsureRoot();
            CloseRoot();
            _root!.Result!.GasUsed = gasSpent.SpentGas;
            // The processor marks an included frame transaction successful whatever its frames did; its receipt
            // derives the status from the frames instead.
            if (_frameReceipts is not null && TxFrameReceipt.AggregateStatus(_frameReceipts) != TxFrameReceipt.StatusSuccess)
            {
                _root.Error = FrameFailedError;
            }
        }

        public void MarkFailed(in GasConsumed gasSpent, byte[] output, string? error)
        {
            EnsureRoot();
            CloseRoot();
            // A frame transaction fails at the transaction level, so the reason is the root's; its figures stay.
            _root!.Error = error;
            _root.Result!.GasUsed = gasSpent.SpentGas;
            _root.Result.Output = output;
        }

        public void OnChildPushed(ParityTraceAction parent, ParityTraceAction child)
        {
            if (parent != _root) return;

            // A frame is a top-level invocation, so a precompile frame is kept like a top-level precompile call.
            child.IncludeInTrace = true;
            _lastFrameAction = child;
            if (tracer.IsTracingInstructions)
            {
                tracer.OnEnterFrame(child);
            }
        }

        /// <summary>The result a failed action keeps: a reverted frame keeps its gasUsed and revert output, as a
        /// REVERT frame does in the execution-apis trace profile; anything else keeps none.</summary>
        public ParityTraceResult? FailedActionResult(ParityTraceAction action, EvmExceptionType error, ulong gasLeft, ReadOnlyMemory<byte> output)
        {
            _failedActionGasLeft = gasLeft;
            if (error != EvmExceptionType.Revert || !IsFrame(action)) return null;

            ParityTraceResult result = action.Result!;
            result.GasUsed = action.Gas - gasLeft;
            result.Output = output.ToArray();
            return result;
        }

        public void OnPopped(ParityTraceAction action)
        {
            if (!IsFrame(action) || !tracer.IsTracingInstructions) return;
            tracer.OnLeaveFrame(action.Result is null ? _failedActionGasLeft : action.Gas - action.Result.GasUsed);
        }

        private bool IsFrame(ParityTraceAction action) => _root is not null && action.TraceAddress.Length == 1;

        public void AddFrameOperation(ParityTraceAction frame)
        {
            _lastFrameOperation = new ParityVmOperationTrace { Pc = frame.TraceAddress.AsSpan()[0], Cost = frame.Gas };
            tracer._currentVmTrace.Ops.Add(_lastFrameOperation);
            tracer._currentOperation = _lastFrameOperation;
        }

        public void CloseFrameOperation(ulong gasLeft)
        {
            _lastFrameOperation!.Used = gasLeft;
            tracer._treatGasParityStyle = false;
        }

        public void PlaceFrameOperation(int frameIndex)
        {
            if (_lastFrameOperation is null) return;
            _lastFrameOperation.Pc = frameIndex;
            _lastFrameOperation = null;
        }

        public void ReportFrameEnd(int frameIndex, EvmExceptionType? error)
        {
            if ((uint)frameIndex >= (uint)_frames.Length) return;

            _frameActions ??= new ParityTraceAction?[_frames.Length];
            _frameErrors ??= new EvmExceptionType?[_frames.Length];
            _frameActions[frameIndex] = _lastFrameAction;
            _frameErrors[frameIndex] = error;
            _lastFrameAction = null;
            tracer.OnFrameEnd(frameIndex);
        }

        public void ReportFrameTxReceipt(TxFrameReceipt[] frameReceipts)
        {
            // The validation-prefix simulation reports no receipts, which pin no frame's outcome.
            if (frameReceipts.Length == 0 || _framesOrdered) return;
            _framesOrdered = true;
            _frameReceipts = frameReceipts;

            EnsureRoot();
            int frameCount = Math.Min(frameReceipts.Length, _frames.Length);
            // The processor reports every frame's end before the receipts, so each dispatched frame is claimed here.
            List<ParityTraceAction> ordered = new(frameCount);
            for (int i = 0; i < frameCount; i++)
            {
                ParityTraceAction action = _frameActions?[i] ?? BuildUndispatchedFrameAction(_frames[i], frameReceipts[i], _frameErrors?[i]);
                action.Gas = _frames[i].GasLimit;
                action.Result?.GasUsed = frameReceipts[i].GasUsed;
                MoveToFramePosition(action, i);
                ordered.Add(action);
            }

            _root!.Subtraces = ordered;
        }

        private ParityTraceAction CreateRoot()
        {
            ParityTraceAction action = tracer.RentAction();
            action.Type = "call";
            action.CallType = "call";
            action.From = tx.SenderAddress;
            action.To = Eip8141Constants.EntryPointAddress;
            action.Input = CappedArray<byte>.Empty;
            // GasLimit carries only the frame limits, short of the intrinsic gas the transaction also spends.
            action.Gas = tracer._spec is not null && FrameTxValidation.TryCalculateGasBudget(tx, tracer._spec, out _, out _, out ulong maxGas)
                ? maxGas
                : tx.GasLimit;
            return action;
        }

        private ParityTraceAction BuildUndispatchedFrameAction(TxFrame frame, TxFrameReceipt receipt, EvmExceptionType? error)
        {
            ParityTraceAction action = tracer.RentAction();
            action.TraceAddress = tracer.RentTraceAddress(1);
            action.Type = "call";
            action.CallType = frame.Mode is FrameMode.Verify or FrameMode.PostTx ? "staticcall" : "call";
            action.From = frame.Mode == FrameMode.Sender ? tx.SenderAddress : Eip8141Constants.EntryPointAddress;
            action.To = frame.Target ?? tx.SenderAddress;
            action.Value = frame.Value;
            action.Input = tracer.CopyInput(frame.Data);
            action.Error = receipt.Status switch
            {
                TxFrameReceipt.StatusSuccess => null,
                TxFrameReceipt.StatusSkipped => SkippedFrameError,
                _ => GetErrorDescription(error ?? EvmExceptionType.Revert)
            };

            if (action.Error is null)
            {
                action.Result!.Output = [];
            }
            else
            {
                action.Result = null;
            }

            return action;
        }

        private static void MoveToFramePosition(ParityTraceAction action, int position)
        {
            Span<int> traceAddress = action.TraceAddress.AsSpan();
            if (traceAddress[0] == position) return;
            traceAddress[0] = position;
            foreach (ParityTraceAction subtrace in action.Subtraces)
            {
                MoveToFramePosition(subtrace, position);
            }
        }
    }
}
