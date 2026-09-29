// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing;

[Parallelizable(ParallelScope.All)]
public class GasEstimatorTests
{
    private const ulong RequestedGas = 1_000_000;
    private static readonly byte[] CallData = [0x01];

    [TestCase(100_000ul, 101_510ul, true, TestName = "Gap of 1510 is within 1.5% of the upper bound")]
    [TestCase(100_000ul, 101_522ul, true, TestName = "Gap of 1522 is still within 1.5% of the upper bound")]
    [TestCase(100_000ul, 101_523ul, false, TestName = "Gap of 1523 exceeds 1.5% of the upper bound")]
    public void IsWithinErrorRatio_measures_the_gap_against_the_upper_bound(ulong lo, ulong hi, bool expected) =>
        Assert.That(GasEstimator.IsWithinErrorRatio(lo, hi, GasEstimator.DefaultErrorMargin / 10000d), Is.EqualTo(expected),
            $"({hi} - {lo}) / {hi} compared with 1.5%");

    [TestCase(21_000ul, 1_000_000ul, 42_000ul, TestName = "Midpoint far above the lower bound is capped at twice it")]
    [TestCase(500_000ul, 600_000ul, 550_000ul, TestName = "Midpoint within twice the lower bound is kept")]
    public void NextGasLimit_skews_the_bisection_to_the_low_side(ulong lo, ulong hi, ulong expected) =>
        Assert.That(GasEstimator.NextGasLimit(lo, hi), Is.EqualTo(expected), "next gas limit to try");

    [Test]
    public void Estimate_plain_transfer_returns_the_gas_its_single_run_used()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 20_000, peakGas: 20_000, requiredGasLimit: GasCostOf.Transaction);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(RequestedGas).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.Null);
            Assert.That(estimation.Gas, Is.EqualTo(20_000ul), "the transfer run's gas used, not the base cost");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { GasCostOf.Transaction }), "one run at the base transaction cost");
        }
    }

    [Test]
    public void Estimate_transfer_to_a_contract_is_searched()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 30_000);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(RequestedGas).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.Null);
            Assert.That(processor.GasLimits[0], Is.EqualTo(RequestedGas), "a recipient with code skips the transfer run and probes the highest gas limit");
        }
    }

    [TestCase(1_000ul, TestName = "Value equal to the balance")]
    [TestCase(1_001ul, TestName = "Value above the balance")]
    public void Estimate_priced_transaction_whose_value_is_not_below_the_balance_fails_without_running(ulong value)
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 21_000, peakGas: 21_000, requiredGasLimit: GasCostOf.Transaction);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(RequestedGas).WithGasPrice(1).WithValue(value).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(1_000, isContract: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo(GasEstimator.InsufficientBalance));
            Assert.That(processor.GasLimits, Is.Empty, "the balance check precedes every run");
        }
    }

    [Test]
    public void Estimate_caps_the_highest_gas_limit_at_what_the_balance_pays_for()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 55_000, peakGas: 55_000, requiredGasLimit: 60_000);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(RequestedGas)
            .WithGasPrice(10).WithValue(10).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(500_010, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo($"{GasEstimator.GasExceedsAllowanceMsgPrefix} (50000)"), "(500010 - 10) / 10 gas is fundable");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { 50_000ul }), "only the probe at the allowance runs");
        }
    }

    [Test]
    public void Estimate_optimistic_guess_that_succeeds_becomes_the_upper_bound()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 31_000);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Gas, Is.EqualTo(31_053ul), "bisection stops once the gap is within 1.5% of the upper bound");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { RequestedGas, 32_812ul, 31_405ul, 30_702ul, 31_053ul }),
                "probe, the (30000 + 2300) * 64 / 63 guess, then bisection below it");
        }
    }

    [Test]
    public void Estimate_far_above_the_gas_used_doubles_the_lower_bound_until_it_succeeds()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 200_000);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Gas, Is.EqualTo(200_973ul), "first gas limit within 1.5% of the requirement");
            Assert.That(processor.GasLimits, Is.EqualTo(new[]
            {
                RequestedGas, 32_812ul, 65_624ul, 131_248ul, 262_496ul, 196_872ul, 229_684ul, 213_278ul, 205_075ul, 200_973ul, 198_922ul
            }), "each midpoint above twice the lower bound is capped at twice it");
        }
    }

    [Test]
    public void Estimate_refunded_transaction_needs_its_peak_gas_not_its_gas_used()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 40_000, peakGas: 60_000, requiredGasLimit: 60_000);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Gas, Is.EqualTo(60_376ul), "within 1.5% above the 60000 peak");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { RequestedGas, 63_288ul, 51_643ul, 57_465ul, 60_376ul, 58_920ul, 59_648ul }),
                "the search starts at the 40000 gas used and the guess is taken from the 60000 peak");
        }
    }

    [Test]
    public void Estimate_zero_error_margin_bisects_to_the_exact_requirement()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 31_000);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true), errorMargin: 0);

        Assert.That(estimation.Gas, Is.EqualTo(31_000ul), "no error ratio stops the search before the bounds meet");
    }

    [Test]
    public void Estimate_revert_at_the_highest_gas_limit_returns_its_revert_data()
    {
        byte[] revertData = [0xde, 0xad];
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, failure: EvmExceptionType.Revert, output: revertData);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Reverted, Is.True, "a revert is reported as such");
            Assert.That(estimation.RevertData, Is.EqualTo(revertData), "revert data of the run at the highest gas limit");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { RequestedGas }), "a revert at the highest gas limit ends the estimate");
        }
    }

    [Test]
    public void Estimate_out_of_gas_at_the_highest_gas_limit_reports_the_allowance()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        Assert.That(estimation.Error, Is.EqualTo($"{GasEstimator.GasExceedsAllowanceMsgPrefix} ({RequestedGas})"), "out of gas at the highest gas limit");
    }

    [Test]
    public void Estimate_rejected_transaction_reports_the_gas_limit_it_was_rejected_at()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 30_000, rejection: TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo(TransactionResult.InsufficientMaxFeePerGasForSenderBalance.ErrorDescription));
            Assert.That(estimation.RejectedGasLimit, Is.EqualTo(RequestedGas), "the probe's gas limit");
        }
    }

    [Test]
    public void Estimate_below_intrinsic_gas_at_the_highest_gas_limit_reports_the_allowance()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 30_000, rejection: TransactionResult.GasLimitBelowIntrinsicGas);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo($"{GasEstimator.GasExceedsAllowanceMsgPrefix} ({RequestedGas})"), "raising the gas limit would fix it");
            Assert.That(estimation.RejectedGasLimit, Is.Null, "not a rejection of the transaction itself");
        }
    }

    [TestCase(16_777_217ul, TestName = "Above the per-transaction cap")]
    [TestCase(33_554_432ul, TestName = "Twice the per-transaction cap")]
    public void Estimate_under_eip7825_caps_the_highest_gas_limit(ulong requestedGas)
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(requestedGas).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true), spec: Osaka.Instance, blockGasLimit: requestedGas);

        Assert.That(processor.GasLimits, Is.EqualTo(new[] { Eip7825Constants.DefaultTxGasLimitCap }), "the probe runs at the EIP-7825 cap");
    }

    [Test]
    public void Estimate_gas_below_the_base_cost_is_bounded_by_the_block_gas_limit()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(GasCostOf.Transaction - 1).WithGasPrice(0).TestObject;

        Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true), blockGasLimit: 7_000_000);

        Assert.That(processor.GasLimits, Is.EqualTo(new[] { 7_000_000ul }), "the probe runs at the block gas limit");
    }

    [Test]
    public void Estimate_execution_failure_at_the_highest_gas_limit_is_rejected_at_the_funded_gas_limit()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, failure: EvmExceptionType.PrecompileFailure);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(RequestedGas).WithGasPrice(10).WithValue(10).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(5_000_010, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo(EvmExceptionType.PrecompileFailure.GetEvmExceptionDescription()));
            Assert.That(estimation.RejectedGasLimit, Is.EqualTo(500_000ul), "the requested gas capped by the (5000010 - 10) / 10 the balance funds");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { 500_000ul }), "the probe already ran at the funded gas limit");
        }
    }

    [Test]
    public void Estimate_execution_failure_above_the_per_transaction_cap_is_named_at_the_requested_gas()
    {
        const ulong requestedGas = 30_000_000;
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, failure: EvmExceptionType.PrecompileFailure);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(requestedGas).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true), spec: Osaka.Instance, blockGasLimit: requestedGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.RejectedGasLimit, Is.EqualTo(requestedGas), "named at the requested gas, not the EIP-7825 cap");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { Eip7825Constants.DefaultTxGasLimitCap, requestedGas }), "probe at the cap, then the run the failure is named by");
        }
    }

    [Test]
    public void Estimate_execution_failure_with_a_request_below_the_intrinsic_cost_reports_the_processor_error()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, failure: EvmExceptionType.PrecompileFailure);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(1_000).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true), blockGasLimit: RequestedGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo(nameof(EvmExceptionType.PrecompileFailure)), "the processor's own description of the failure");
            Assert.That(estimation.RejectedGasLimit, Is.Null, "not named with a gas limit");
        }
    }

    [TestCase(EvmExceptionType.InvalidJumpDestination, TestName = "Invalid jump destination")]
    [TestCase(EvmExceptionType.AccessViolation, TestName = "Return data out of bounds")]
    [TestCase(EvmExceptionType.StaticCallViolation, TestName = "Write protection")]
    [TestCase(EvmExceptionType.TransactionCollision, TestName = "Contract address collision")]
    [TestCase(EvmExceptionType.InvalidCode, TestName = "Invalid code")]
    public void Estimate_execution_failure_with_shared_text_is_reported_as_is(EvmExceptionType failure)
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, failure: failure);

        GasEstimation estimation = Estimate(processor, CreateCall(), CreateStateProvider(UInt256.Zero, isContract: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo(failure.GetEvmExceptionDescription()));
            Assert.That(estimation.RejectedGasLimit, Is.Null, "not named with a gas limit");
        }
    }

    [TestCase(true, TestName = "Outermost frame out of gas")]
    [TestCase(false, TestName = "Out of gas after the outermost frame completed")]
    public void Estimate_creation_out_of_gas_is_an_allowance_failure_only_when_its_frame_ran_out(bool frameRanOut)
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: ulong.MaxValue, reportsFrameFailure: frameRanOut);
        Transaction tx = Build.A.Transaction.WithCode(CallData).WithGasLimit(RequestedGas).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: false));

        Assert.That(estimation.Error, Is.EqualTo(frameRanOut ? $"{GasEstimator.GasExceedsAllowanceMsgPrefix} ({RequestedGas})" : "out of gas"));
    }

    [Test]
    public void Estimate_caps_the_highest_gas_limit_at_the_gas_cap()
    {
        ScriptedTransactionProcessor processor = new(gasUsed: 30_000, peakGas: 30_000, requiredGasLimit: 30_000);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(20_000).WithGasPrice(0).TestObject;

        GasEstimation estimation = Estimate(processor, tx, CreateStateProvider(UInt256.Zero, isContract: true), gasCap: 20_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.EqualTo($"{GasEstimator.GasExceedsAllowanceMsgPrefix} (20000)"), "a request below the base cost falls back to the block gas limit, then the gas cap");
            Assert.That(processor.GasLimits, Is.EqualTo(new[] { 20_000ul }));
        }
    }

    [Test]
    public void Failure_text_rerun_cut_short_by_cancellation_keeps_the_processor_text()
    {
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(_ => throw new OperationCanceledException());
        BlockExecutionContext blockContext = new(Build.A.BlockHeader.TestObject, London.Instance);
        string error = EvmExceptionType.BadInstruction.GetEvmExceptionDescription()!;

        bool described = ExecutionFailureText.TryDescribe(processor, CreateCall(), in blockContext, EvmExceptionType.BadInstruction, error, CancellationToken.None, out string text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False, "a cancelled rerun names no operation");
            Assert.That(text, Is.EqualTo(error), "the processor's text is kept");
        }
    }

    private static Transaction CreateCall() =>
        Build.A.Transaction.WithTo(TestItem.AddressB).WithData(CallData).WithGasLimit(RequestedGas).WithGasPrice(0).TestObject;

    private static IReadOnlyStateProvider CreateStateProvider(UInt256 balance, bool isContract)
    {
        IReadOnlyStateProvider stateProvider = Substitute.For<IReadOnlyStateProvider>();
        stateProvider.GetBalance(Arg.Any<Address>()).Returns(balance);
        stateProvider.IsContract(Arg.Any<Address>()).Returns(isContract);
        return stateProvider;
    }

    private static GasEstimation Estimate(
        ITransactionProcessor processor,
        Transaction tx,
        IReadOnlyStateProvider stateProvider,
        ulong errorMargin = GasEstimator.DefaultErrorMargin,
        IReleaseSpec? spec = null,
        ulong blockGasLimit = 30_000_000,
        ulong gasCap = 0)
    {
        BlockHeader header = Build.A.BlockHeader.WithGasLimit(blockGasLimit).TestObject;
        return new GasEstimator(processor, stateProvider).Estimate(tx, new BlockExecutionContext(header, spec ?? London.Instance), errorMargin, gasCap);
    }

    /// <summary>Succeeds at or above <paramref name="requiredGasLimit"/> and runs out of gas below it.</summary>
    private sealed class ScriptedTransactionProcessor(
        ulong gasUsed,
        ulong peakGas,
        ulong requiredGasLimit,
        EvmExceptionType failure = EvmExceptionType.OutOfGas,
        byte[]? output = null,
        TransactionResult? rejection = null,
        bool reportsFrameFailure = true) : ITransactionProcessor
    {
        public List<ulong> GasLimits { get; } = [];

        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            GasLimits.Add(transaction.GasLimit);

            if (rejection is { } rejected)
                return rejected;

            if (transaction.GasLimit < GasCostOf.Transaction)
                return TransactionResult.GasLimitBelowIntrinsicGas;

            if (txTracer.IsTracingActions)
                txTracer.ReportAction(transaction.GasLimit, transaction.Value, transaction.SenderAddress!, transaction.To ?? Address.Zero, transaction.Data, ExecutionType.CREATE);

            if (transaction.GasLimit < requiredGasLimit)
            {
                if (txTracer.IsTracingActions && reportsFrameFailure)
                    txTracer.ReportActionError(failure);

                txTracer.MarkAsFailed(transaction.To ?? Address.Zero, new GasConsumed(transaction.GasLimit, transaction.GasLimit), output ?? [], failure.ToString());
                return TransactionResult.EvmException(failure);
            }

            if (txTracer.IsTracingActions)
                txTracer.ReportActionEnd(0, ReadOnlyMemory<byte>.Empty);

            txTracer.MarkAsSuccess(transaction.To ?? Address.Zero, new GasConsumed(gasUsed, gasUsed, MaxUsedGas: peakGas), [], []);
            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader) { }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) { }
    }
}
