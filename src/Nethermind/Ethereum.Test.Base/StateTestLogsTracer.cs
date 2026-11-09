// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Test.Base;

/// <summary>
/// Collects the logs of the receipt a state test's transaction gets, for the check against the fixture's
/// <c>logs</c> hash.
/// </summary>
/// <remarks>
/// It traces receipts and reads no output, as the block processor's receipt tracer does when nothing else traces.
/// A transaction rejected before it runs reports no receipt and keeps the empty log list.
/// </remarks>
internal sealed class StateTestLogsTracer : TxTracer, IFrameTxReceiptTracer, ITxOutputTracingFilter
{
    private LogEntry[] _logs = [];
    private TxFrameReceipt[]? _frameReceipts;

    public override bool IsTracingReceipt => true;

    public bool IsTracingOutput => false;

    /// <summary>keccak256 of the RLP list of the collected logs, the value a state test's <c>logs</c> holds.</summary>
    /// <remarks>
    /// Transfers, reverts and rejected transactions leave no logs, whose list hash is a constant; other lists are
    /// encoded straight into the hasher rather than into an intermediate array.
    /// </remarks>
    public Hash256 LogsHash
    {
        get
        {
            if (_logs.Length == 0) return Keccak.OfAnEmptySequenceRlp;

            KeccakRlpWriter writer = new();
            LogEntryDecoder.Instance.Encode(ref writer, _logs);
            return writer.GetHash();
        }
    }

    /// <summary>The tracer to run the transaction with, so that <paramref name="txTracer"/> still gets every callback.</summary>
    public ITxTracer Wrap(ITxTracer txTracer) =>
        ReferenceEquals(txTracer, NullTxTracer.Instance) ? this : new CompositeTxTracer(txTracer, this);

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        _logs = logs;

    // EIP-7906: a failed ASSERT frame discards the body but keeps the validation prefix, whose logs stay in the
    // frame receipts, as BlockReceiptsTracer builds the failed receipt.
    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
        _logs = _frameReceipts is null ? [] : TxFrameReceipt.ConcatLogs(_frameReceipts);

    public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => _frameReceipts = frameReceipts;
}
