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
}
