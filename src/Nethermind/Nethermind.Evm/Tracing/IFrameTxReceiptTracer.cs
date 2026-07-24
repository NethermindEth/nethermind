// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Optional receipt-tracer capability for EIP-8141 frame transactions: the transaction processor
/// reports the payer and the per-frame receipts before marking the transaction, so the receipts
/// tracer can attach them to the built <see cref="TxReceipt"/>.
/// </summary>
public interface IFrameTxReceiptTracer
{
    void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts);
}
