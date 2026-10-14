// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuraWithdrawalProcessorTests
{
    [Test]
    public void Should_invoke_contract_as_expected()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        ILogManager logManager = Substitute.For<ILogManager>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, logManager);
        Block block = Build.A.Block
            .WithNumber(123)
            .WithWithdrawals(Build.A.Withdrawal
                    .WithAmount(1_000_000UL)
                    .WithRecipient(Address.SystemUser).TestObject, Build.A.Withdrawal
                    .WithAmount(2_000_000UL)
                    .WithRecipient(Address.Zero).TestObject)
            .TestObject;
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();

        spec.WithdrawalsEnabled.Returns(true);

        // we need to capture those values, because the ArrayPools will be disposed before we can match them
        ulong[] values = [];
        Address[] addresses = [];
        contract.ExecuteWithdrawals(
            block.Header,
            4,
            Arg.Do<IList<ulong>>(a => values = a.ToArray()),
            Arg.Do<IList<Address>>(a => addresses = a.ToArray()));

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract.Received(1).ExecuteWithdrawals(
            Arg.Is(block.Header),
            Arg.Is<UInt256>(4),
            Arg.Is<IList<ulong>>(a => values.AsEnumerable().SequenceEqual(new[] { 1_000_000UL, 2_000_000UL })),
            Arg.Is<IList<Address>>(a => addresses.AsEnumerable().SequenceEqual(new[] { Address.SystemUser, Address.Zero })));
    }

    [Test]
    public void Should_report_contract_failure_as_invalid_block()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, LimboLogs.Instance);
        Block block = BuildBlockWithWithdrawal();
        contract.When(c => c.ExecuteWithdrawals(block.Header, Arg.Any<UInt256>(), Arg.Any<IList<ulong>>(), Arg.Any<IList<Address>>()))
            .Throw(new ContractCallFailedException("System call to executeSystemWithdrawals returned error 'revert'"));

        Assert.Throws<InvalidBlockException>(() => withdrawalProcessor.ProcessWithdrawals(block, WithdrawalsSpec()));
    }

    [Test]
    public void Should_not_report_other_exception_as_invalid_block([ValueSource(nameof(NodeLocalFaults))] Exception exception)
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, LimboLogs.Instance);
        Block block = BuildBlockWithWithdrawal();
        contract.When(c => c.ExecuteWithdrawals(block.Header, Arg.Any<UInt256>(), Arg.Any<IList<ulong>>(), Arg.Any<IList<Address>>()))
            .Throw(exception);

        Assert.That(() => withdrawalProcessor.ProcessWithdrawals(block, WithdrawalsSpec()), Throws.Exception.SameAs(exception));
    }

    [Test]
    public void Should_report_failed_withdrawals_system_call_as_invalid_block()
    {
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        transactionProcessor
            .When(p => p.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()))
            .Do(ci => ci.Arg<ITxTracer>().MarkAsFailed(Address.Zero, default, [], "revert"));
        AuraWithdrawalProcessor withdrawalProcessor = new(new WithdrawalContract(transactionProcessor, AbiEncoder.Instance, Address.Zero), LimboLogs.Instance);

        Assert.That(() => withdrawalProcessor.ProcessWithdrawals(BuildBlockWithWithdrawal(), WithdrawalsSpec()),
            Throws.InstanceOf<InvalidBlockException>().With.InnerException.InstanceOf<ContractCallFailedException>());
    }

    [Test]
    public void Should_not_report_withdrawals_system_call_execution_fault_as_invalid_block()
    {
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        transactionProcessor
            .When(p => p.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()))
            .Do(_ => throw new InvalidOperationException("state read failed"));
        AuraWithdrawalProcessor withdrawalProcessor = new(new WithdrawalContract(transactionProcessor, AbiEncoder.Instance, Address.Zero), LimboLogs.Instance);

        Assert.That(() => withdrawalProcessor.ProcessWithdrawals(BuildBlockWithWithdrawal(), WithdrawalsSpec()),
            Throws.TypeOf<AbiException>().With.InnerException.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Should_not_invoke_contract_before_Shanghai()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        ILogManager logManager = Substitute.For<ILogManager>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, logManager);
        Block block = Build.A.Block.TestObject;
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();

        spec.WithdrawalsEnabled.Returns(false);

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract.Received(0).ExecuteWithdrawals(
            Arg.Any<BlockHeader>(),
            Arg.Any<UInt256>(),
            Arg.Any<IList<ulong>>(),
            Arg.Any<IList<Address>>());
    }

    [Test]
    public void Should_not_invoke_contract_for_genesis()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        ILogManager logManager = Substitute.For<ILogManager>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, logManager);
        Block block = Build.A.Block
            .Genesis
            .WithWithdrawals([])
            .TestObject;
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();

        spec.WithdrawalsEnabled.Returns(true);

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract.Received(0).ExecuteWithdrawals(
            Arg.Any<BlockHeader>(),
            Arg.Any<UInt256>(),
            Arg.Any<IList<ulong>>(),
            Arg.Any<IList<Address>>());
    }

    private static IEnumerable<Exception> NodeLocalFaults()
    {
        yield return new AbiException("System call to executeSystemWithdrawals returned an exception", new InvalidOperationException("state read failed"));
        yield return new AbiException("Cannot encode withdrawal arguments");
        yield return new ArgumentNullException("blockHeader");
    }

    private static Block BuildBlockWithWithdrawal() => Build.A.Block
        .WithNumber(123)
        .WithWithdrawals(Build.A.Withdrawal.WithAmount(1_000_000UL).WithRecipient(Address.Zero).TestObject)
        .TestObject;

    private static IReleaseSpec WithdrawalsSpec()
    {
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.WithdrawalsEnabled.Returns(true);
        return spec;
    }
}
