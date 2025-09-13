// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    public class GasEstimationTests
    {
        private readonly ExecutionType _executionType;

        public GasEstimationTests(bool useCreates)
        {
            _executionType = useCreates ? ExecutionType.CREATE : ExecutionType.CALL;
        }

        [Test]
        public void Does_not_take_into_account_precompiles()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.CALL, true);
            testEnvironment.tracer.ReportActionEnd(400,
                Array.Empty<byte>()); // this would not happen but we want to ensure that precompiles are ignored
            testEnvironment.tracer.ReportActionEnd(600, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(0);
            Assert.That(err, Is.EqualTo("Transaction execution fails"));
        }

        [Test]
        public void Only_traces_actions_and_receipts()
        {
            EstimateGasTracer tracer = new();
            (tracer.IsTracingActions && tracer.IsTracingReceipt).Should().BeTrue();
            (tracer.IsTracingBlockHash
             || tracer.IsTracingState
             || tracer.IsTracingStorage
             || tracer.IsTracingCode
             || tracer.IsTracingInstructions
             || tracer.IsTracingMemory
             || tracer.IsTracingStack
             || tracer.IsTracingOpLevelStorage).Should().BeFalse();
        }

        [Test]
        public void Handles_well_top_level()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportActionEnd(600, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(0);
            Assert.That(err, Is.EqualTo("Transaction execution fails"));
        }

        [Test]
        public void Handles_well_serial_calls()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>());
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(14L);
            Assert.That(err, Is.Null);
        }

        [Test]
        public void Handles_well_errors()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Other);
                testEnvironment.tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Other);
                testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(24L);
            Assert.That(err, Is.Null);
        }

        [Test]
        public void Handles_well_revert()
        {
            using TestEnvironment testEnvironment = new();
            long gasLimit = 100_000_000;
            Transaction tx = Build.A.Transaction.WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            long gasLeft = gasLimit - 22000;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);

            testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 96000000);
            testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 98000000);
            testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 99000000);
            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(35146L);
            Assert.That(err, Is.Null);
        }

        [Test]
        public void Easy_one_level_case()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(128).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            testEnvironment.tracer.ReportActionEnd(63, Array.Empty<byte>()); // second level
            testEnvironment.tracer.ReportActionEnd(65, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? _).Should().Be(1);
        }

        [Test]
        public void Handles_well_precompile_out_of_gas()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(128).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.TRANSACTION);
            testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType);
            testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, true);

            Action reportError = () => testEnvironment.tracer.ReportActionError(EvmExceptionType.OutOfGas);

            reportError.Should().NotThrow();
            reportError.Should().NotThrow();
            reportError.Should().NotThrow();
        }

        [Test]
        public void Handles_well_nested_calls_where_most_nested_defines_excess()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(18);
            Assert.That(err, Is.Null);
        }

        [Test]
        public void Handles_well_nested_calls_where_least_nested_defines_excess()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(300, Address.Zero, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err).Should().Be(17);
            Assert.That(err, Is.Null);
        }

        [TestCase(-1)]
        [TestCase(10000)]
        [TestCase(10001)]
        public void Estimate_UseErrorMarginOutsideBounds_ThrowArgumentOutOfRangeException(int errorMargin)
        {
            Transaction tx = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(tx).TestObject;
            EstimateGasTracer tracer = new();
            tracer.MarkAsSuccess(Address.Zero, 1, [], []);
            IReadOnlyStateProvider stateProvider = Substitute.For<IReadOnlyStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(new UInt256(1));
            GasEstimator sut = new GasEstimator(
                Substitute.For<ITransactionProcessor>(),
                stateProvider,
                MainnetSpecProvider.Instance,
                new BlocksConfig());

            sut.Estimate(tx, block.Header, tracer, out string? err, errorMargin);
            Assert.That(err, Is.Not.Null);
        }

        [TestCase(Transaction.BaseTxGasCost, GasEstimator.DefaultErrorMargin, false)]
        [TestCase(Transaction.BaseTxGasCost, 100, false)]
        [TestCase(Transaction.BaseTxGasCost, 1000, false)]
        [TestCase(Transaction.BaseTxGasCost + 10000, GasEstimator.DefaultErrorMargin, true)]
        [TestCase(Transaction.BaseTxGasCost + 20000, GasEstimator.DefaultErrorMargin, true)]
        [TestCase(Transaction.BaseTxGasCost + 123456789, 123, true)]
        public void Estimate_DifferentAmountOfGasAndMargin_EstimationResultIsWithinMargin(int totalGas, int errorMargin, bool fail)
        {
            Transaction tx = Build.A.Transaction.WithGasLimit(30000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
            EstimateGasTracer tracer = new();
            tracer.MarkAsSuccess(Address.Zero, totalGas, [], []);
            IReadOnlyStateProvider stateProvider = Substitute.For<IReadOnlyStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(new UInt256(1));
            GasEstimator sut = new GasEstimator(
                Substitute.For<ITransactionProcessor>(),
                stateProvider,
                MainnetSpecProvider.Instance,
                new BlocksConfig());

            long result = sut.Estimate(tx, block.Header, tracer, out string? err, errorMargin);

            if (fail)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(err, Is.EqualTo("Cannot estimate gas, gas spent exceeded transaction and block gas limit"));
                    Assert.That(result, Is.EqualTo(0));
                }
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(err, Is.Null);
                    Assert.That(result, Is.EqualTo(totalGas).Within(totalGas * (errorMargin / 10000d + 1)));
                }
            }
        }

        [Test]
        public void Estimate_UseErrorMargin_EstimationResultIsNotExact()
        {
            Transaction tx = Build.A.Transaction.WithGasLimit(30000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
            EstimateGasTracer tracer = new();
            const int totalGas = Transaction.BaseTxGasCost;
            tracer.MarkAsSuccess(Address.Zero, totalGas, [], []);
            IReadOnlyStateProvider stateProvider = Substitute.For<IReadOnlyStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(new UInt256(1));
            GasEstimator sut = new GasEstimator(
                Substitute.For<ITransactionProcessor>(),
                stateProvider,
                MainnetSpecProvider.Instance,
                new BlocksConfig());

            long result = sut.Estimate(tx, block.Header, tracer, out string? err);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(err, Is.Null);
                Assert.That(result, Is.Not.EqualTo(totalGas).Within(10));
            }
        }

        [Test]
        public void Estimate_UseZeroErrorMargin_EstimationResultIsExact()
        {
            Transaction tx = Build.A.Transaction.WithGasLimit(30000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
            EstimateGasTracer tracer = new();
            const int totalGas = Transaction.BaseTxGasCost;
            tracer.MarkAsSuccess(Address.Zero, totalGas, [], []);
            IReadOnlyStateProvider stateProvider = Substitute.For<IReadOnlyStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(new UInt256(1));
            GasEstimator sut = new GasEstimator(
                Substitute.For<ITransactionProcessor>(),
                stateProvider,
                MainnetSpecProvider.Instance,
                new BlocksConfig());

            long result = sut.Estimate(tx, block.Header, tracer, out string? err, 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(err, Is.Null);
                Assert.That(result, Is.EqualTo(totalGas));
            }
        }

        [Test]
        public void Should_return_zero_when_out_of_gas_detected_during_estimation()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(100000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);

            testEnvironment.tracer.ReportActionError(EvmExceptionType.OutOfGas);

            testEnvironment.tracer.MarkAsSuccess(Address.Zero, 500, Array.Empty<byte>(), Array.Empty<LogEntry>());

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when OutOfGas is detected");
            err.Should().NotBeNull("Error message should be provided when OutOfGas is detected");
            testEnvironment.tracer.OutOfGas.Should().BeTrue("OutOfGas should be set to true");
        }

        [Test]
        public void Should_return_zero_when_status_code_is_failure()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(100000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);

            testEnvironment.tracer.MarkAsFailed(Address.Zero, 500, Array.Empty<byte>(), "execution failed");

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when StatusCode is Failure");
            err.Should().NotBeNull("Error message should be provided when transaction always fails");
            testEnvironment.tracer.StatusCode.Should().Be(StatusCode.Failure);
        }

        [Test]
        public void Should_return_positive_estimate_when_no_failure_conditions()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(128).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.CALL, false);
            testEnvironment.tracer.ReportActionEnd(63, Array.Empty<byte>());
            testEnvironment.tracer.ReportActionEnd(65, Array.Empty<byte>());

            testEnvironment.tracer.MarkAsSuccess(Address.Zero, 63, Array.Empty<byte>(), Array.Empty<LogEntry>());

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);
            estimate.Should().Be(1, "Should match the Easy_one_level_case result");
            err.Should().BeNull("No error should occur");
            testEnvironment.tracer.OutOfGas.Should().BeFalse("No OutOfGas should be detected");
            testEnvironment.tracer.StatusCode.Should().Be(StatusCode.Success, "StatusCode should be Success");
        }

        [Test]
        public void Should_return_zero_with_insufficient_balance_error_when_sender_is_address_zero_with_value_transfer()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000)
                .WithSenderAddress(Address.Zero)
                .WithValue(1.Ether()) // Value transfer with zero balance
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            // Address.Zero has zero balance by default in test environment
            EstimateGasTracer tracer = new();
            tracer.MarkAsFailed(Address.Zero, 0, Array.Empty<byte>(), "insufficient balance");

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when Address.Zero has insufficient balance for value transfer");
            err.Should().Be("insufficient balance", "Should provide insufficient balance error message");
        }

        [Test]
        public void Should_return_zero_with_out_of_gas_error_when_address_zero_runs_out_of_gas()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000)
                .WithSenderAddress(Address.Zero)
                .WithValue(1.Ether())
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            EstimateGasTracer tracer = new();
            tracer.ReportAction(100000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.TRANSACTION, false);
            tracer.ReportActionError(EvmExceptionType.OutOfGas);
            tracer.MarkAsFailed(Address.Zero, 100000, Array.Empty<byte>(), "out of gas");

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when Address.Zero transaction runs out of gas");
            err.Should().Be("Gas estimation failed due to out of gas", "Should provide out of gas error message");
        }

        [Test]
        public void Should_return_zero_with_execution_failure_when_address_zero_transaction_always_fails()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000)
                .WithSenderAddress(Address.Zero)
                .WithValue(1.Ether())
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            EstimateGasTracer tracer = new();
            tracer.ReportAction(100000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.TRANSACTION, false);
            tracer.MarkAsFailed(Address.Zero, 50000, Array.Empty<byte>(), "execution reverted");

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when Address.Zero transaction always fails");
            err.Should().Be("execution reverted", "Should provide the specific execution failure message");
        }

        [Test]
        public void Should_succeed_when_address_zero_has_no_value_transfer()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000)
                .WithSenderAddress(Address.Zero)
                .WithValue(0) // No value transfer - should work even with zero balance
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(100000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportActionEnd(79000, Array.Empty<byte>());
            testEnvironment.tracer.MarkAsSuccess(Address.Zero, 21000, Array.Empty<byte>(), Array.Empty<LogEntry>());

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            estimate.Should().BeGreaterThan(0, "Should succeed when Address.Zero has no value transfer");
            err.Should().BeNull("No error should occur for Address.Zero with no value transfer");
        }

        [Test]
        public void Should_return_zero_when_address_zero_exceeds_gas_limits()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(21000) // Very low gas limit
                .WithSenderAddress(Address.Zero)
                .WithValue(0)
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(21000).TestObject;

            EstimateGasTracer tracer = new();
            // Simulate gas spent exceeding available limits
            tracer.ReportAction(21000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.TRANSACTION, false);
            tracer.MarkAsSuccess(Address.Zero, 25000, Array.Empty<byte>(), Array.Empty<LogEntry>());

            long estimate = testEnvironment.estimator.Estimate(tx, block.Header, tracer, out string? err);

            estimate.Should().Be(0, "Should return 0 when gas spent exceeds limits");
            err.Should().Be("Cannot estimate gas, gas spent exceeded transaction and block gas limit",
                "Should provide gas limit exceeded error message");
        }

        [Test]
        public void Should_estimate_gas_successfully_ignoring_precompile_costs()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(30000).WithSenderAddress(TestItem.AddressA).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(30000, 0, TestItem.AddressA, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportAction(28000, 0, TestItem.AddressA, Address.Zero, Array.Empty<byte>(),
                ExecutionType.CALL, true);
            testEnvironment.tracer.ReportActionEnd(26000, Array.Empty<byte>());
            testEnvironment.tracer.ReportActionEnd(25000, Array.Empty<byte>());

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);
            result.Should().BeGreaterThan(0, "Should estimate positive gas, ignoring precompile costs");
            Assert.That(err, Is.Null);
        }

        [Test]
        public void Should_estimate_gas_successfully_for_simple_transaction()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(30000).WithSenderAddress(TestItem.AddressA).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(30000, 0, TestItem.AddressA, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);
            testEnvironment.tracer.ReportActionEnd(28000, Array.Empty<byte>());

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);
            result.Should().BeGreaterThan(0, "Should estimate positive gas for successful transaction");
            Assert.That(err, Is.Null);
        }

        [TestCase(50_000, false)]
        [TestCase(500_000, false)]
        [TestCase(1_000_000, false)]
        [TestCase(1_100_000, true)]
        public void Should_estimate_gas_for_explicit_gas_check_and_revert(long gasLimit, bool shouldSucceed)
        {
            TestEnvironment testEnvironment = new();
            Address contractAddress = TestItem.AddressB;
            var check = 1_000_000;
            byte[] contractCode = Bytes.FromHexString($"0x62{check:x6}5a10600f576001600055005b6000806000fd");
            testEnvironment.InsertContract(contractAddress, contractCode);

            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1) // Ensure opcode `REVERT` is available
                .WithTransactions(tx).TestObject;
            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            if (shouldSucceed)
            {
                result.Should().BeGreaterThan(1_000_000, "Gas estimation should account for the gas threshold in the contract");
                err.Should().BeNull();
            }
            else
            {
                err.Should().NotBeNull("Gas estimation should fail when the gas limit is too low");
            }
        }

        private class TestEnvironment : IDisposable
        {
            public ISpecProvider _specProvider;
            public IEthereumEcdsa _ethereumEcdsa;
            public TransactionProcessor _transactionProcessor;
            public IWorldState _stateProvider;
            public EstimateGasTracer tracer;
            public GasEstimator estimator;
            private readonly IDisposable _closer;

            public TestEnvironment()
            {
                _specProvider = MainnetSpecProvider.Instance;
                IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
                _stateProvider = worldStateManager.GlobalWorldState;
                _closer = _stateProvider.BeginScope(IWorldState.PreGenesis);
                _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);

                EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
                VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
                _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
                _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

                tracer = new();
                BlocksConfig blocksConfig = new();
                estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);
            }

            public void InsertContract(Address contractAddress, byte[] code)
            {
                _stateProvider.CreateAccount(contractAddress, 0);
                _stateProvider.InsertCode(contractAddress, ValueKeccak.Compute(code), code, _specProvider.GenesisSpec);
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);
            }

            public void Dispose()
            {
                _closer.Dispose();
            }
        }
    }
}
