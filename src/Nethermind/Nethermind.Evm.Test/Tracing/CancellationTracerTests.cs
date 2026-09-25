// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
        public void Throws_operation_canceled_when_token_is_cancelled()
        {
            using CancellationTokenSource cancellationTokenSource = new();
            cancellationTokenSource.Cancel();
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationTokenSource.Token) { IsTracingActions = true };

            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionError(EvmExceptionType.None));
            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionRemainingGas(1));
        }

        [Test]
        public async Task Does_not_throw_if_cancellation_token_is_default()
        {
            CancellationToken cancellationToken = default;
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationToken) { IsTracingActions = true };

            await Task.Delay(2000, cancellationToken);

            Assert.DoesNotThrow(() => tracer.ReportActionError(EvmExceptionType.None));
        }

        /// <summary>
        /// With instruction tracing, a started instruction's callbacks are all delivered after cancellation; it lands
        /// at the next start instead. Without it, every callback checks.
        /// </summary>
        [Test]
        public void Defers_cancellation_to_the_next_instruction_start_only_when_tracing_instructions([Values] bool isTracingInstructions)
        {
            using CancellationTokenSource cancellationTokenSource = new();
            CancellationTxTracer tracer = CancellableInstructionTracer(cancellationTokenSource.Token, isTracingInstructions);

            tracer.StartOperation(0, Instruction.STOP, 1, default);
            cancellationTokenSource.Cancel();

            if (isTracingInstructions)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.DoesNotThrow(() => tracer.ReportRefund(1), "callback of a started instruction");
                    Assert.DoesNotThrow(() => tracer.ReportOperationRemainingGas(1), "completion of a started instruction");
                    Assert.DoesNotThrow(() => tracer.ReportOperationError(EvmExceptionType.OutOfGas), "error of a completed instruction");
                    Assert.DoesNotThrow(() => tracer.ReportGasUpdateForVmTrace(0, 1), "gas update of a resumed instruction");
                }
                tracer.InnerTracer.Received(1).ReportOperationError(EvmExceptionType.OutOfGas);
            }
            else
            {
                Assert.Throws<OperationCanceledException>(() => tracer.ReportRefund(1));
            }

            Assert.Throws<OperationCanceledException>(() => tracer.StartOperation(1, Instruction.STOP, 1, default));
        }

        [Test]
        public void Throws_operation_canceled_when_an_instruction_never_completes()
        {
            using CancellationTokenSource cancellationTokenSource = new();
            CancellationTxTracer tracer = CancellableInstructionTracer(cancellationTokenSource.Token);

            tracer.StartOperation(0, Instruction.STOP, 1, default);
            cancellationTokenSource.Cancel();

            Assert.Throws<OperationCanceledException>(() => tracer.MarkAsFailed(TestItem.AddressA, default, [], "error"));
            Assert.Throws<OperationCanceledException>(() => tracer.MarkAsSuccess(TestItem.AddressA, default, [], []));
        }

        [Test]
        public void Throws_on_instruction_start_before_forwarding_it_when_cancelled()
        {
            using CancellationTokenSource cancellationTokenSource = new();
            CancellationTxTracer tracer = CancellableInstructionTracer(cancellationTokenSource.Token);

            cancellationTokenSource.Cancel();

            Assert.Throws<OperationCanceledException>(() => tracer.StartOperation(1, Instruction.STOP, 1, default));
            Assert.That(tracer.InnerTracer.ReceivedCalls().Select(static call => call.GetMethodInfo().Name),
                Does.Not.Contain(nameof(ITxTracer.StartOperation)));
        }

        private static CancellationTxTracer CancellableInstructionTracer(CancellationToken token, bool isTracingInstructions = true)
        {
            ITxTracer innerTracer = Substitute.For<ITxTracer>();
            innerTracer.IsTracingInstructions.Returns(isTracingInstructions);
            return new CancellationTxTracer(innerTracer, token) { IsTracingReceipt = true, IsTracingRefunds = true };
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
