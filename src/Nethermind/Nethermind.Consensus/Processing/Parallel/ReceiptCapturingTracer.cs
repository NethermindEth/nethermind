// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Lightweight tracer that captures the receipt data from <see cref="ITxTracer.MarkAsSuccess"/>
/// and <see cref="ITxTracer.MarkAsFailed"/> during parallel execution.
/// </summary>
public class ReceiptCapturingTracer : TxTracer
{
    public CapturedReceipt Captured { get; private set; }

    public override bool IsTracingReceipt
    {
        get => true;
        protected set { }
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        Captured = new CapturedReceipt(true, recipient, gasSpent, output, logs, null, stateRoot);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        Captured = new CapturedReceipt(false, recipient, gasSpent, output, null, error, stateRoot);
    }
}

public readonly record struct CapturedReceipt(
    bool IsSuccess,
    Address Recipient,
    GasConsumed GasConsumed,
    byte[] Output,
    LogEntry[]? Logs,
    string? Error,
    Hash256? StateRoot);
