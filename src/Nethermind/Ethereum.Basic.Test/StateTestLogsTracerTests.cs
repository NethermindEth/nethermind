// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

/// <summary>
/// The state-test logs tracer must hash the RLP list of the logs it collected, take the logs of a frame transaction whose
/// <c>ASSERT</c> frame failed as the block processor's receipt does, and its composite must pass the frame reports on to
/// the caller's tracer as the processor would.
/// </summary>
public class StateTestLogsTracerTests
{
    private static readonly LogEntry PrefixLog = new(TestItem.AddressA, [1, 2, 3], [TestItem.KeccakA]);

    [TestCaseSource(nameof(LogLists))]
    public void Logs_hash_is_keccak_of_the_log_list_rlp(LogEntry[] logs)
    {
        StateTestLogsTracer logsTracer = new();
        logsTracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21_000, 21_000), [], logs);

        Assert.That(logsTracer.LogsHash, Is.EqualTo(Keccak.Compute(LogEntryDecoder.Instance.Encode(logs).Bytes)));
    }

    /// <remarks>
    /// The cases take every RLP length form: single-byte, short and long data strings, and log entries and lists on
    /// both sides of the 55-byte boundary, the longest list with a three-byte length.
    /// </remarks>
    private static IEnumerable<TestCaseData> LogLists()
    {
        yield return LogList("no logs");
        yield return LogList("one log", PrefixLog);
        yield return LogList("short list", new LogEntry(TestItem.AddressB, [0x01], []));
        yield return LogList("several logs with topics and large data",
            new LogEntry(TestItem.AddressA, [], []),
            new LogEntry(TestItem.AddressB, [0x7f], [TestItem.KeccakA]),
            new LogEntry(TestItem.AddressC, Data(55), [TestItem.KeccakA, TestItem.KeccakB]),
            new LogEntry(TestItem.AddressD, Data(56), [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC]),
            new LogEntry(TestItem.AddressE, Data(70_000), [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD]));

        static TestCaseData LogList(string name, params LogEntry[] logs) => new TestCaseData((object)logs).SetArgDisplayNames(name);

        static byte[] Data(int length) => Enumerable.Range(1, length).Select(static i => (byte)i).ToArray();
    }

    [Test]
    public void Failed_assert_frame_keeps_the_prefix_logs_as_the_block_receipt_does([Values] bool callerTraces)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        BlockReceiptsTracer blockTracer = new();
        blockTracer.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);
        blockTracer.StartNewTxTrace(tx);
        ReportFailedAssert(blockTracer);
        blockTracer.EndTxTrace();
        LogEntry[] receiptLogs = blockTracer.LastReceipt.Logs;

        StateTestLogsTracer logsTracer = new();
        RecordingFrameTracer caller = new();
        ReportFailedAssert(logsTracer.Wrap(callerTraces ? caller : NullTxTracer.Instance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptLogs, Is.EqualTo(new[] { PrefixLog }));
            Assert.That(logsTracer.LogsHash, Is.EqualTo(Keccak.Compute(LogEntryDecoder.Instance.Encode(receiptLogs).Bytes)));
            Assert.That(caller.Events, Is.EqualTo(callerTraces ? new[] { "receipt 3", "failed" } : System.Array.Empty<string>()));
        }
    }

    /// <remarks>
    /// The processor sends the receipt report only to a tracer that traces receipts, and frame ends and rollbacks to any
    /// frame tracer; a caller's frame tracer is found through a wrapper as the processor's own lookup does.
    /// </remarks>
    [Test]
    public void Composite_passes_frame_reports_to_the_callers_tracer([Values] bool tracesReceipts, [Values] bool wrapped)
    {
        RecordingFrameTracer caller = new(tracesReceipts);
        ITxTracer callerTracer = wrapped ? new WrappingTracer(caller) : caller;
        IFrameTxReceiptTracer frameTracer = new StateTestLogsTracer().Wrap(callerTracer) as IFrameTxReceiptTracer;

        Assert.That(frameTracer, Is.Not.Null);
        frameTracer.ReportFrameEnd(2, EvmExceptionType.Revert);
        frameTracer.ReportFramesRolledBack(1, 2);
        frameTracer.ReportFrameTxReceipt(TestItem.AddressC, FrameReceipts());

        Assert.That(caller.Events, Is.EqualTo(tracesReceipts
            ? new[] { "end 2 Revert", "rolledback 1 2", "receipt 3" }
            : new[] { "end 2 Revert", "rolledback 1 2" }));
    }

    /// <summary>The validation prefix logged; the body's logs were scrubbed by the rollback; the ASSERT frame failed.</summary>
    private static TxFrameReceipt[] FrameReceipts() =>
    [
        new(TxFrameReceipt.StatusSuccess, 30_000, 0, [PrefixLog]),
        new(TxFrameReceipt.StatusSuccess, 20_000, 0, []),
        new(TxFrameReceipt.StatusFailure, 10_000, 0, []),
    ];

    /// <summary>The calls <c>TransactionProcessorBase.SettleFrameTx</c> makes when an <c>ASSERT</c> frame reverted.</summary>
    private static void ReportFailedAssert(ITxTracer tracer)
    {
        (tracer as IFrameTxReceiptTracer)?.ReportFrameTxReceipt(TestItem.AddressC, FrameReceipts());
        if (tracer.IsTracingReceipt)
        {
            tracer.MarkAsFailed(Eip8141Constants.EntryPointAddress, new GasConsumed(60_000, 60_000), [], "ASSERT frame reverted");
        }
    }

    private sealed class RecordingFrameTracer(bool tracesReceipts = true) : TxTracer, IFrameTxReceiptTracer
    {
        public List<string> Events { get; } = [];

        public override bool IsTracingReceipt => tracesReceipts;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256 stateRoot = null) =>
            Events.Add("success");

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string error, Hash256 stateRoot = null) =>
            Events.Add("failed");

        public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => Events.Add($"receipt {frameReceipts.Length}");

        public void ReportFrameEnd(int frameIndex, EvmExceptionType? error) => Events.Add($"end {frameIndex} {error}");

        public void ReportFramesRolledBack(int fromFrameIndex, int toFrameIndex) => Events.Add($"rolledback {fromFrameIndex} {toFrameIndex}");
    }

    private sealed class WrappingTracer(ITxTracer inner) : TxTracer, ITxTracerWrapper
    {
        public ITxTracer InnerTracer => inner;

        public override bool IsTracingReceipt => inner.IsTracingReceipt;
    }
}
