// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing;

/// <summary>Optional receipt-tracer capability: the processor reports an EIP-8141 payer and per-frame
/// receipts before marking the transaction, for attaching to the built <see cref="TxReceipt"/>.</summary>
public interface IFrameTxReceiptTracer
{
    /// <summary>Reports the transaction's payer and one receipt per frame, once every frame has run.</summary>
    /// <param name="payer">The account the transaction's gas was charged to.</param>
    /// <param name="frameReceipts">One receipt per <c>tx.Frames</c> entry, in frame order; empty from the
    /// validation-prefix simulation, which pins no frame's outcome.</param>
    void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts);

    /// <summary>Reports the frame at <paramref name="frameIndex"/> completing, so a tracer can align what it
    /// observed with the per-frame receipts reported at the end of the transaction.</summary>
    /// <remarks>A frame that fails before dispatch, and a <c>VERIFY</c> frame running the default code, never
    /// enter the VM, so the action callbacks alone do not say which frame produced which observations.</remarks>
    /// <param name="error">The EVM error that ended the frame, or <c>null</c> when it succeeded.</param>
    void ReportFrameEnd(int frameIndex, EvmExceptionType? error) { }

    /// <summary>Reports that the frames from <paramref name="fromFrameIndex"/> up to the failed frame at
    /// <paramref name="toFrameIndex"/> had their state and logs rolled back, by an atomic batch unroll or a failed
    /// <c>POST_TX</c> frame.</summary>
    /// <remarks>The rolled-back frames keep their success status, and where a <c>POST_TX</c> rollback starts
    /// depends on when the payer was approved, so the receipts alone do not say which frames were discarded.</remarks>
    /// <param name="fromFrameIndex">The first rolled-back frame.</param>
    /// <param name="toFrameIndex">The frame whose failure caused the rollback, which already reported its end.</param>
    void ReportFramesRolledBack(int fromFrameIndex, int toFrameIndex) { }

    /// <summary>The first tracer in <paramref name="tracer"/>'s wrapper chain that takes EIP-8141 frame reports,
    /// or <see langword="null"/> when none does.</summary>
    /// <remarks>The tracing RPCs hand the processor a wrapped tracer, so the capability is reached through
    /// the wrapper chain rather than on the outermost one. A <see cref="CompositeTxTracer"/> is not a wrapper
    /// and ends the walk; no tracing RPC builds one, and a chain that did would need this to fan out.</remarks>
    static IFrameTxReceiptTracer? FindIn(ITxTracer tracer)
    {
        while (true)
        {
            if (tracer is IFrameTxReceiptTracer frameTxTracer) return frameTxTracer;
            if (tracer is not ITxTracerWrapper wrapper) return null;
            tracer = wrapper.InnerTracer;
        }
    }
}
