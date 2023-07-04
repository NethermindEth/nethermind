// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
        [Retry(3)]
        public async Task Throw_operation_canceled_after_given_timeout()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(10);
            using CancellationTokenSource cancellationTokenSource = new(timeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationToken) { IsTracingActions = true };

            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(100);

            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionError(EvmExceptionType.None));
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
            CancellationBlockTracer blockTracer = new CancellationBlockTracer(Substitute.For<IBlockTracer>());
            Transaction transaction = Build.A.Transaction.TestObject;
            blockTracer.StartNewTxTrace(transaction).Should().BeOfType<CancellationTxTracer>();
        }
    }
}
