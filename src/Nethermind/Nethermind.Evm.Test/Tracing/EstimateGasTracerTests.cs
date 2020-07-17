//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture(true)]
    [TestFixture(false)]
    public class EstimateGasTracerTests
    {
        private readonly ExecutionType _executionType;

        public EstimateGasTracerTests(bool useCreates)
        {
            _executionType = useCreates ? ExecutionType.Create : ExecutionType.Call;
        }

        [Test]
        public void Does_not_take_into_account_precompiles()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Call, true);
            tracer.ReportActionEnd(400, Array.Empty<byte>()); // this would not happen but we want to ensure that precompiles are ignored
            tracer.ReportActionEnd(600, Array.Empty<byte>());
            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(0);
        }

        [Test]
        public void Only_traces_actions_and_receipts()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            (tracer.IsTracingActions && tracer.IsTracingReceipt).Should().BeTrue();
            (tracer.IsTracingBlockHash
             || tracer.IsTracingState
             || tracer.IsTracingCode
             || tracer.IsTracingInstructions
             || tracer.IsTracingMemory
             || tracer.IsTracingStack
             || tracer.IsTracingOpLevelStorage).Should().BeFalse();
        }

        [Test]
        public void Handles_well_top_level()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportActionEnd(600, Array.Empty<byte>());
            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(0);
        }

        [Test]
        public void Handles_well_serial_calls()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            tracer.ReportActionEnd(400, Array.Empty<byte>());
            tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            if (_executionType.IsAnyCreate())
            {
                tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                tracer.ReportActionEnd(300, Array.Empty<byte>());
            }
            else
            {
                tracer.ReportActionEnd(200, Array.Empty<byte>());
                tracer.ReportActionEnd(300, Array.Empty<byte>()); // should not happen
            }

            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(14L);
        }

        [Test]
        public void Handles_well_errors()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                tracer.ReportActionError(EvmExceptionType.Other);
                tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                tracer.ReportActionError(EvmExceptionType.Other);
                tracer.ReportActionEnd(400, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(24L);
        }

        [Test]
        public void Easy_one_level_case()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            tracer.ReportActionEnd(63, Array.Empty<byte>()); // second level
            tracer.ReportActionEnd(65, Array.Empty<byte>());

            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(128).TestObject).Should().Be(1);
        }

        [Test]
        public void Handles_well_nested_calls_where_most_nested_defines_excess()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>()); // second level
                tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                tracer.ReportActionEnd(200, Array.Empty<byte>()); // second level
                tracer.ReportActionEnd(400, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(18);
        }

        [Test]
        public void Handles_well_nested_calls_where_least_nested_defines_excess()
        {
            EstimateGasTracer tracer = new EstimateGasTracer();
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                tracer.ReportActionEnd(300, Address.Zero, Array.Empty<byte>()); // second level
                tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                tracer.ReportActionEnd(300, Array.Empty<byte>()); // second level
                tracer.ReportActionEnd(200, Array.Empty<byte>());
                tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            tracer.CalculateEstimate(Build.A.Transaction.WithGasLimit(1000).TestObject).Should().Be(17);
        }

        [Test]
        public void Throw_operation_canceled_after_given_timeout()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(100);
            CancellationToken cancellationToken = new CancellationTokenSource(timeout).Token;
            Transaction transactionMock = Substitute.For<Transaction>();
            var tracer = new EstimateGasTracer(cancellationToken);

            Thread.Sleep(timeout.Add(TimeSpan.FromMilliseconds(100)));

            Assert.Throws<OperationCanceledException>(() => tracer.CalculateEstimate(transactionMock));
        }

        [Test]
        public void Does_not_throw_if_cancellation_token_is_default()
        {
            CancellationToken cancellationToken = default(CancellationToken);
            var tracer = new EstimateGasTracer(cancellationToken);
            Transaction transactionMock = Substitute.For<Transaction>();
            
            Thread.Sleep(TimeSpan.FromSeconds(2));

            Assert.DoesNotThrow(() => tracer.CalculateEstimate(transactionMock)); 
        }
    }
}