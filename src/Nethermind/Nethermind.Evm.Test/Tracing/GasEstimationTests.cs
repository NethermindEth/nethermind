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
                    Assert.That(err, Is.EqualTo("Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap"));
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
        public void Estimate_simple_transfer_with_errorMargin_should_be_exact()
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
                Assert.That(result, Is.EqualTo(totalGas));
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
                .WithGasPrice(0)
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
            err.Should().Be("Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap",
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
                .WithData([0x00, 0x00, 0x00, 0x00])
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
                result.Should().BeGreaterThan(1_000_000,
                    "Gas estimation should account for the gas threshold in the contract");
                err.Should().BeNull();
            }
            else
            {
                err.Should().NotBeNull("Gas estimation should fail when the gas limit is too low");
            }
        }

        [Test]
        public void Should_succeed_with_internal_revert()
        {
            using TestEnvironment testEnvironment = new();
            long gasLimit = 100_000;
            Transaction tx = Build.A.Transaction.WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            long gasLeft = gasLimit - 22000;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);

            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.CALL, false);

            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.CALL, false);

            testEnvironment.tracer.ReportActionRevert(gasLeft - 1000, Array.Empty<byte>());
            testEnvironment.tracer.ReportActionEnd(gasLeft - 500, Array.Empty<byte>());
            testEnvironment.tracer.ReportActionEnd(gasLeft, Array.Empty<byte>());
            testEnvironment.tracer.MarkAsSuccess(Address.Zero, 25000, Array.Empty<byte>(), Array.Empty<LogEntry>());

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0);
            err.Should().BeNull();
            testEnvironment.tracer.TopLevelRevert.Should().BeFalse();
            testEnvironment.tracer.OutOfGas.Should().BeFalse();
        }

        [Test]
        public void Should_fail_with_top_level_revert()
        {
            using TestEnvironment testEnvironment = new();
            long gasLimit = 100_000;
            Transaction tx = Build.A.Transaction.WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            long gasLeft = gasLimit - 22000;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.TRANSACTION, false);

            testEnvironment.tracer.ReportActionRevert(gasLeft - 1000, Array.Empty<byte>());
            testEnvironment.tracer.MarkAsFailed(Address.Zero, 25000, Array.Empty<byte>(), "execution reverted");

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().Be(0);
            err.Should().Be("execution reverted");
            testEnvironment.tracer.TopLevelRevert.Should().BeTrue();
        }

        [Test]
        public void Should_estimate_gas_when_inner_call_reverts_but_transaction_succeeds()
        {
            // Reproduces https://github.com/NethermindEth/nethermind/issues/10552
            // GnosisSafe createProxyWithNonce has inner calls that revert (try/catch pattern).
            // The bug: ReportOperationError sets OutOfGas=true for ANY revert, even inner ones,
            // causing the binary search in gas estimation to always think the tx failed.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            Address callerAddress = TestItem.AddressC;

            // Reverter contract: always reverts with empty data
            byte[] reverterCode = Prepare.EvmCode
                .PushData(0x00)
                .PushData(0x00)
                .Op(Instruction.REVERT)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            // Caller contract: CALLs reverter (which reverts), catches the revert, then succeeds.
            // This simulates GnosisSafe's try/catch pattern.
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 100_000)  // inner call that reverts - return value 0 on stack
                .Op(Instruction.POP)             // discard call result
                .PushData(0x01)                  // value = 1
                .PushData(0x00)                  // key = 0
                .Op(Instruction.SSTORE)          // store 1 at slot 0 (proves execution continued)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            long gasLimit = 300_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed when inner call reverts but transaction succeeds overall");
            err.Should().BeNull("No error should occur - inner reverts should not be treated as top-level failures");
        }

        [Test]
        public void Should_estimate_gas_for_create2_with_setup_call_pattern()
        {
            // Simulates GnosisSafe createProxyWithNonce: CREATE2 deploys a proxy, then
            // the caller does a CALL to the newly deployed proxy for setup.
            // The setup call may revert internally but the overall tx succeeds.
            using TestEnvironment testEnvironment = new();

            // The "proxy" runtime code: just stores a value (simulating successful setup)
            byte[] proxyRuntimeCode = Prepare.EvmCode
                .PushData(0x42)    // value
                .PushData(0x00)    // key
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            // Init code that returns the runtime code
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(proxyRuntimeCode)
                .Done;

            // Factory contract: CREATE2 the proxy, then CALL setup on it
            // CREATE2(value=0, offset=0, size=initCode.length, salt=0)
            // CALL(gas, addr_from_create2, value=0, ...)
            byte[] factoryCode = Prepare.EvmCode
                .Create2(initCode, new byte[] { 0x01 }, 0) // CREATE2 with salt=1
                .Op(Instruction.DUP1)       // duplicate address for CALL
                .PushData(0x00)             // retSize
                .PushData(0x00)             // retOffset
                .PushData(0x00)             // argSize
                .PushData(0x00)             // argOffset
                .PushData(0x00)             // value
                .Op(Instruction.SWAP5)      // bring address to top (after value)
                .PushData(50_000)           // gas for setup call
                .Op(Instruction.CALL)
                .Op(Instruction.POP)        // discard call result
                .Op(Instruction.POP)        // discard remaining address copy
                .Op(Instruction.STOP)
                .Done;

            Address factoryAddress = TestItem.AddressB;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ConstantinopleFixBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed for CREATE2 + setup call pattern");
            err.Should().BeNull("No error for CREATE2 + setup call");
        }

        [Test]
        public void Should_estimate_gas_with_multiple_inner_calls_mixed_reverts()
        {
            // Contract makes 3 inner calls: first reverts, second succeeds, third reverts.
            // Transaction should still succeed and gas estimation should work.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            Address succeederAddress = TestItem.AddressC;

            // Contract that always reverts
            byte[] reverterCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            // Contract that succeeds (stores value)
            byte[] succeederCode = Prepare.EvmCode
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(succeederAddress, succeederCode);

            // Caller: calls reverter, succeeder, reverter - catches all failures
            Address callerAddress = TestItem.AddressD;
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 30_000)   // call 1: reverts
                .Op(Instruction.POP)
                .Call(succeederAddress, 50_000)  // call 2: succeeds
                .Op(Instruction.POP)
                .Call(reverterAddress, 30_000)   // call 3: reverts
                .Op(Instruction.POP)
                .PushData(0xFF)
                .PushData(0x01)
                .Op(Instruction.SSTORE)          // store to prove we got here
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed with mixed inner reverts");
            err.Should().BeNull("No error when inner calls revert but overall tx succeeds");
        }

        [Test]
        public void Should_estimate_gas_when_inner_call_runs_out_of_gas_but_caller_handles_it()
        {
            // Verifies that inner OOG (caught by the caller) does not fail gas estimation.
            // OutOfGas must be nesting-aware (only set at top level), matching Geth behavior.
            // Geth's binary search checks result.Failed() which only reflects the top-level outcome.
            // See: https://github.com/ethereum/go-ethereum/blob/master/eth/gasestimator/gasestimator.go
            using TestEnvironment testEnvironment = new();

            // Contract that consumes all gas via infinite loop (will always OOG)
            Address gasGuzzlerAddress = TestItem.AddressB;
            byte[] gasGuzzlerCode = Prepare.EvmCode
                .Op(Instruction.JUMPDEST)   // offset 0
                .PushData((byte)0x00)
                .Op(Instruction.JUMP)       // jump back to 0
                .Done;
            testEnvironment.InsertContract(gasGuzzlerAddress, gasGuzzlerCode);

            // Middle contract: calls gas guzzler with limited gas, catches OOG
            Address middleAddress = TestItem.AddressC;
            byte[] middleCode = Prepare.EvmCode
                .Call(gasGuzzlerAddress, 1_000)  // only 1000 gas - will OOG
                .Op(Instruction.POP)             // discard result (0 = failure)
                .PushData(0x01)
                .PushData((byte)0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(middleAddress, middleCode);

            // Outer caller
            Address callerAddress = TestItem.AddressD;
            byte[] callerCode = Prepare.EvmCode
                .Call(middleAddress, 100_000)
                .Op(Instruction.POP)
                .PushData(0x02)
                .PushData(0x01)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed when inner call OOGs but caller handles it");
            err.Should().BeNull("No error - inner OOG should not affect top-level estimation");
        }

        [TestCase(50_000, true)]
        [TestCase(500_000, true)]
        [TestCase(1_000, false)]
        public void Should_estimate_gas_with_gas_sensitive_branching(long gasThreshold, bool shouldSucceed)
        {
            // Contract that checks gasleft() and branches: if gasleft >= threshold, SSTORE; else REVERT.
            // Tests that the binary search correctly handles gas-dependent execution paths.
            using TestEnvironment testEnvironment = new();

            Address contractAddress = TestItem.AddressB;

            // Use the existing pattern from Should_estimate_gas_for_explicit_gas_check_and_revert
            // Bytecode: PUSH3 <threshold>, GAS, LT, PUSH1 <revert_pc>, JUMPI, PUSH1 1, PUSH1 0, SSTORE, STOP, JUMPDEST, PUSH1 0, PUSH1 0, REVERT
            var check = gasThreshold;
            byte[] contractCode = Bytes.FromHexString($"0x62{check:x6}5a10600f576001600055005b6000806000fd");
            testEnvironment.InsertContract(contractAddress, contractCode);

            long gasLimit = 1_100_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            if (shouldSucceed)
            {
                result.Should().BeGreaterThan(0, "Gas estimation should find enough gas for the success path");
                err.Should().BeNull("No error - binary search should find gas level above threshold");
            }
            else
            {
                result.Should().BeGreaterThan(0, "Low threshold should always succeed");
                err.Should().BeNull();
            }
        }

        [Test]
        public void Should_estimate_gas_for_create_with_constructor_making_calls()
        {
            // CREATE deploys a contract whose constructor makes an external CALL.
            // The constructor call might revert but CREATE still succeeds.
            using TestEnvironment testEnvironment = new();

            // External contract that reverts
            Address externalAddress = TestItem.AddressB;
            byte[] externalCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(externalAddress, externalCode);

            // Runtime code (deployed contract's code)
            byte[] runtimeCode = Prepare.EvmCode
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            // Init code: calls external (which reverts, but init code catches it), then returns runtime code
            byte[] initCode = Prepare.EvmCode
                .Call(externalAddress, 10_000)
                .Op(Instruction.POP)             // discard call result
                .ForInitOf(runtimeCode)
                .Done;

            // Factory: CREATE with init code, then STOP
            Address factoryAddress = TestItem.AddressC;
            byte[] factoryCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.POP)             // discard created address
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed for CREATE with constructor that makes calls");
            err.Should().BeNull("No error for constructor-call pattern");
        }

        [Test]
        public void Should_estimate_gas_consistently_across_repeated_calls()
        {
            // Tests that repeated gas estimation on the same contract yields consistent results.
            // The EstimateGasTracer is reused across binary search iterations - state must reset properly.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            byte[] reverterCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            Address callerAddress = TestItem.AddressC;
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 30_000)
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            long gasLimit = 300_000;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithGasLimit(gasLimit)
                .TestObject;

            long? firstResult = null;
            for (int i = 0; i < 10; i++)
            {
                // Each estimation uses a fresh tracer (as BlockchainBridge.EstimateGas does)
                TestEnvironment freshEnv = new();
                freshEnv.InsertContract(reverterAddress, reverterCode);
                freshEnv.InsertContract(callerAddress, callerCode);

                Transaction tx = Build.A.Transaction
                    .WithGasLimit(gasLimit)
                    .WithTo(callerAddress)
                    .WithSenderAddress(TestItem.AddressA)
                    .TestObject;

                long result = freshEnv.estimator.Estimate(tx, block.Header, freshEnv.tracer, out string? err);

                result.Should().BeGreaterThan(0, $"Iteration {i}: gas estimation should succeed");
                err.Should().BeNull($"Iteration {i}: no error expected");

                firstResult ??= result;
                result.Should().Be(firstResult.Value, $"Iteration {i}: result should be consistent");

                freshEnv.Dispose();
            }
        }

        [Test]
        public void Should_estimate_gas_for_deeply_nested_calls()
        {
            // Chain of 4 nested CALLs to test nesting level tracking in EstimateGasTracer.
            // A -> B -> C -> D (all succeed)
            using TestEnvironment testEnvironment = new();

            // Contract D: leaf, just stores and stops
            Address addrD = TestItem.AddressD;
            byte[] codeD = Prepare.EvmCode
                .PushData(0x04)
                .PushData(0x04)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrD, codeD);

            // Contract C: calls D
            Address addrC = TestItem.AddressC;
            byte[] codeC = Prepare.EvmCode
                .Call(addrD, 50_000)
                .Op(Instruction.POP)
                .PushData(0x03)
                .PushData(0x03)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrC, codeC);

            // Contract B: calls C
            Address addrB = TestItem.AddressB;
            byte[] codeB = Prepare.EvmCode
                .Call(addrC, 100_000)
                .Op(Instruction.POP)
                .PushData(0x02)
                .PushData(0x02)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrB, codeB);

            // Contract A: calls B (this is what the tx calls)
            Address addrA = new("0x0000000000000000000000000000000000000042");
            byte[] codeA = Prepare.EvmCode
                .Call(addrB, 200_000)
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x01)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrA, codeA);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(addrA)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed for deeply nested call chain");
            err.Should().BeNull("No error for deeply nested calls");
        }

        [Test]
        public void Should_estimate_gas_for_nested_create2_with_inner_revert_in_constructor()
        {
            // CREATE2 deploys a contract whose constructor calls an external contract that reverts.
            // Constructor catches the revert and continues. This is the GnosisSafe pattern:
            // createProxyWithNonce -> CREATE2 -> proxy constructor -> setup() call -> possible revert
            using TestEnvironment testEnvironment = new();

            // External contract that always reverts with data
            Address externalAddress = TestItem.AddressB;
            byte[] externalCode = Prepare.EvmCode
                .StoreDataInMemory(0, new byte[] { 0xDE, 0xAD })
                .Revert(2, 0)
                .Done;
            testEnvironment.InsertContract(externalAddress, externalCode);

            // Runtime code (what the proxy becomes after deployment)
            byte[] runtimeCode = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            // Init code: calls external (reverts, caught), then returns runtime code
            byte[] initCode = Prepare.EvmCode
                .Call(externalAddress, 20_000)   // will revert, returns 0
                .Op(Instruction.POP)              // discard failure result
                .ForInitOf(runtimeCode)
                .Done;

            // Factory: CREATE2 with salt, verify address is non-zero, STOP
            Address factoryAddress = TestItem.AddressC;
            byte[] factoryCode = Prepare.EvmCode
                .Create2(initCode, new byte[] { 0xAB, 0xCD }, 0) // CREATE2 with salt
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)           // record success
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            long gasLimit = 500_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ConstantinopleFixBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed for CREATE2 with inner revert in constructor");
            err.Should().BeNull("No error for GnosisSafe-like CREATE2 pattern");
        }

        [Test]
        public void Should_return_revert_error_when_top_level_call_reverts_with_data()
        {
            // Ensures gas estimation properly reports revert data when the top-level call reverts.
            using TestEnvironment testEnvironment = new();

            Address contractAddress = TestItem.AddressB;
            // Store revert reason in memory, then REVERT with it
            byte[] contractCode = Prepare.EvmCode
                .StoreDataInMemory(0, new byte[] { 0x08, 0xC3, 0x79, 0xA0 }) // Error(string) selector
                .Revert(4, 0)
                .Done;
            testEnvironment.InsertContract(contractAddress, contractCode);

            long gasLimit = 300_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().Be(0, "Gas estimation should fail when top-level call reverts");
            err.Should().NotBeNull("Should report an error when top-level reverts");
            // The error contains the revert data (hex-encoded output from the REVERT opcode)
            testEnvironment.tracer.TopLevelRevert.Should().BeTrue("TopLevelRevert should be set for top-level REVERT");
        }

        [Test]
        public void Should_estimate_gas_with_delegatecall_that_reverts_internally()
        {
            // DELEGATECALL that reverts internally - the revert happens in the caller's context
            // but at a nested level. Gas estimation should still succeed.
            using TestEnvironment testEnvironment = new();

            // Implementation that reverts
            Address implAddress = TestItem.AddressB;
            byte[] implCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(implAddress, implCode);

            // Proxy: DELEGATECALL to impl (reverts), catches it, then succeeds
            Address proxyAddress = TestItem.AddressC;
            byte[] proxyCode = Prepare.EvmCode
                .DelegateCall(implAddress, 30_000)
                .Op(Instruction.POP)             // discard result
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(proxyAddress, proxyCode);

            long gasLimit = 300_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(proxyAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            long result = testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer, out string? err);

            result.Should().BeGreaterThan(0, "Gas estimation should succeed when DELEGATECALL reverts but caller handles it");
            err.Should().BeNull("No error for caught DELEGATECALL revert");
        }

        private class TestEnvironment : IDisposable
        {
            public ISpecProvider _specProvider;
            public IEthereumEcdsa _ethereumEcdsa;
            public EthereumTransactionProcessor _transactionProcessor;
            public IWorldState _stateProvider;
            public EstimateGasTracer tracer;
            public GasEstimator estimator;
            private readonly IDisposable _closer;

            public TestEnvironment()
            {
                _specProvider = MainnetSpecProvider.Instance;
                _stateProvider = TestWorldStateFactory.CreateForTest();
                _closer = _stateProvider.BeginScope(IWorldState.PreGenesis);
                _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);

                EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
                EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
                _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
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
