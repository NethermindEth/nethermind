// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxTracerTests
{
    [Test]
    public void ReportOperationRemainingGas_CalledMultipleTimes_OnOperationRemainingGasCalledOnce()
    {
        CountingTracer tracer = new(GethTraceOptions.Default);

        tracer.StartOperation(0, Instruction.ADD, 100, default);
        tracer.ReportOperationRemainingGas(97);
        tracer.ReportOperationRemainingGas(97);
        tracer.ReportOperationRemainingGas(97);

        Assert.That(tracer.OnOperationRemainingGasCallCount, Is.EqualTo(1));
    }

    [Test]
    public void StartOperation_ResetsGuard_AllowsNewGasReport()
    {
        CountingTracer tracer = new(GethTraceOptions.Default);

        tracer.StartOperation(0, Instruction.ADD, 100, default);
        tracer.ReportOperationRemainingGas(97);

        tracer.StartOperation(1, Instruction.MUL, 97, default);
        tracer.ReportOperationRemainingGas(92);

        Assert.That(tracer.OnOperationRemainingGasCallCount, Is.EqualTo(2));
    }

    [Test]
    public void OnOperationRemainingGas_ReceivesCorrectGasValue()
    {
        CountingTracer tracer = new(GethTraceOptions.Default);

        tracer.StartOperation(0, Instruction.ADD, 100, default);
        tracer.ReportOperationRemainingGas(97);

        Assert.That(tracer.LastReportedGas, Is.EqualTo(97));
    }

    private sealed class CountingTracer : GethLikeTxTracer
    {
        public int OnOperationRemainingGasCallCount { get; private set; }
        public long LastReportedGas { get; private set; }

        public CountingTracer(GethTraceOptions options) : base(options) { }

        protected override void OnOperationRemainingGas(long gas)
        {
            OnOperationRemainingGasCallCount++;
            LastReportedGas = gas;
        }
    }
}
