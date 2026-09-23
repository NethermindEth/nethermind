// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [Parallelizable(ParallelScope.All)]
    public class CancellationTracerTests
    {
        [Test]
        public void Forwards_frame_receipts_and_failures()
        {
            ITxTracer inner = Substitute.For<ITxTracer, IFrameTxReceiptTracer>();
            using CancellationTxTracer tracer = new(inner);
            TxFrameReceipt[] receipts = [new(TxFrameReceipt.StatusSuccess, 100, 0, [])];

            tracer.ReportFrameEnd(1, EvmExceptionType.OutOfGas);
            tracer.ReportFrameTxReceipt(TestItem.AddressA, receipts);

            ((IFrameTxReceiptTracer)inner).Received().ReportFrameEnd(1, EvmExceptionType.OutOfGas);
            ((IFrameTxReceiptTracer)inner).Received().ReportFrameTxReceipt(TestItem.AddressA, receipts);
        }

        [Test]
        public void Throws_operation_canceled_when_token_is_cancelled()
        {
            using CancellationTokenSource cancellationTokenSource = new();
            cancellationTokenSource.Cancel();
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationTokenSource.Token) { IsTracingActions = true };

            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionError(EvmExceptionType.None));
            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionRemainingGas(1));
            Assert.Throws<OperationCanceledException>(() => tracer.ReportFrameEnd(0, null));
        }

        [Test]
        public async Task Does_not_throw_if_cancellation_token_is_default()
        {
            CancellationToken cancellationToken = default;
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationToken) { IsTracingActions = true };

            await Task.Delay(2000, cancellationToken);

            Assert.DoesNotThrow(() => tracer.ReportActionError(EvmExceptionType.None));
        }

        [Test]
        public void Creates_inner_tx_cancellation_tracers()
        {
            CancellationBlockTracer blockTracer = new(Substitute.For<IBlockTracer>());
            Transaction transaction = Build.A.Transaction.TestObject;
            Assert.That(blockTracer.StartNewTxTrace(transaction), Is.TypeOf<CancellationTxTracer>());
        }
    }
}
