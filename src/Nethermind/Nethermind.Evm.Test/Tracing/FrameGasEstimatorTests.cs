// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[Parallelizable(ParallelScope.All)]
public class FrameGasEstimatorTests
{
    private const ulong GasCap = 100_000_000;

    [Test]
    public void EstimateFrameGas_KeepsEveryProbeWithinTheWorkBound() =>
        AssertFills(static _ => (200_000, 100_000), errorMargin: 150);

    /// <remarks>Half the frames need more than an even split of the execution room, so they fail their reservation, and
    /// an exact search runs out of probes before the last of them is reached.</remarks>
    [Test]
    public void EstimateFrameGas_FillsFramesThatFailedTheirReservationAfterTheProbeCap() =>
        AssertFills(static i => (i % 2 == 0 ? 400_000UL : 20_000UL, 0), errorMargin: 0);

    private static void AssertFills(Func<int, (ulong Execution, ulong State)> need, int errorMargin)
    {
        FixedNeedProcessor processor = new(need);
        GasEstimator estimator = new(processor, Substitute.For<IReadOnlyStateProvider>(), new TestSpecProvider(Eip8141Prototype.Instance), new BlocksConfig());
        TxFrame[] frames = new TxFrame[Eip8141Constants.MaxFrames];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = new TxFrame(FrameMode.Sender, default, TestItem.AddressB, 0, 0, UInt256.Zero, default);
        Transaction tx = new() { Type = TxType.FrameTx, SenderAddress = TestItem.AddressA, Frames = frames };
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithGasLimit(60_000_000).TestObject;
        bool[] fill = new bool[frames.Length];
        Array.Fill(fill, true);

        Result<TxFrame[]> result = estimator.EstimateFrameGas(tx, new BlockExecutionContext(header, Eip8141Prototype.Instance), fill, fill, GasCap, errorMargin, CancellationToken.None, out _);

        Assert.That(result.IsError, Is.False, result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.MaxExecutionLimits, Is.LessThanOrEqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(processor.MaxTotalLimits, Is.LessThanOrEqualTo(GasCap));
            Assert.That(processor.Probes, Is.LessThanOrEqualTo(512));
            for (int i = 0; i < result.Data!.Length; i++)
            {
                Assert.That(result.Data[i].ExecutionGasLimit, Is.GreaterThanOrEqualTo(need(i).Execution));
                Assert.That(result.Data[i].StateGasLimit, Is.GreaterThanOrEqualTo(need(i).State));
            }
        }
    }

    /// <summary>Runs each frame as succeeding only when its limits cover a fixed need, recording the limits it was given.</summary>
    private sealed class FixedNeedProcessor(Func<int, (ulong Execution, ulong State)> need) : ITransactionProcessor
    {
        public ulong MaxExecutionLimits { get; private set; }
        public ulong MaxTotalLimits { get; private set; }
        public int Probes { get; private set; }

        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            Probes++;
            TxFrame[] frames = transaction.Frames!;
            ulong executionLimits = 0;
            foreach (TxFrame frame in frames) executionLimits += frame.ExecutionGasLimit;
            MaxExecutionLimits = Math.Max(MaxExecutionLimits, executionLimits);
            MaxTotalLimits = Math.Max(MaxTotalLimits, FrameTxValidation.TotalGasLimit(frames) + FrameTxValidation.SignatureVerificationWorkGas(transaction));

            IFrameTxReceiptTracer tracer = (IFrameTxReceiptTracer)txTracer;
            TxFrameReceipt[] receipts = new TxFrameReceipt[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                (ulong execution, ulong state) = need(i);
                bool ok = frames[i].ExecutionGasLimit >= execution && frames[i].StateGasLimit >= state;
                tracer.ReportFrameEnd(i, ok ? null : EvmExceptionType.OutOfGas);
                receipts[i] = new TxFrameReceipt(ok ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure,
                    ok ? execution : frames[i].ExecutionGasLimit, ok ? state : 0, []);
            }

            tracer.ReportFrameTxReceipt(transaction.SenderAddress!, receipts);
            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader) { }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) { }
    }
}
