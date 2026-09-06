// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing;

/// <summary>Optional receipt-tracer capability: the processor reports an EIP-8141 payer and per-frame
/// receipts before marking the transaction, for attaching to the built <see cref="TxReceipt"/>.</summary>
public interface IFrameTxReceiptTracer
{
    void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts);
}
