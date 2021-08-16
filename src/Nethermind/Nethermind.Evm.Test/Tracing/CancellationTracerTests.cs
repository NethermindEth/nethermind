//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
        public async Task Throw_operation_canceled_after_given_timeout()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(10);
            using CancellationTokenSource cancellationTokenSource = new(timeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationToken) {IsTracingActions = true};

            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(100);

            Assert.Throws<OperationCanceledException>(() => tracer.ReportActionError(EvmExceptionType.None));
        }

        [Test]
        public async Task Does_not_throw_if_cancellation_token_is_default()
        {
            CancellationToken cancellationToken = default;
            CancellationTxTracer tracer = new(Substitute.For<ITxTracer>(), cancellationToken) {IsTracingActions = true};
            
            await Task.Delay(2000, cancellationToken);

            Assert.DoesNotThrow(() => tracer.ReportActionError(EvmExceptionType.None)); 
        }
        
        [Test]
        public void Creates_inner_tx_cancellation_tracers()
        {
            var blockTracer = new CancellationBlockTracer(Substitute.For<IBlockTracer>());
            var transaction = Build.A.Transaction.TestObject;
            blockTracer.StartNewTxTrace(transaction).Should().BeOfType<CancellationTxTracer>();
        }
    }
}
