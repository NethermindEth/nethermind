// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Specs.Forks;
using Nethermind.State;
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

    [TestCase("60006000fd", TestName = "Withdrawal_revert_invalidates_block")]
    [TestCase("6401000000005100", TestName = "Withdrawal_memory_expansion_out_of_gas_invalidates_block")]
    [TestCase("fe", TestName = "Withdrawal_invalid_opcode_invalidates_block")]
    public void Should_report_failed_withdrawals_system_call_as_invalid_block(string code) =>
        WithWithdrawalContract(Convert.FromHexString(code), (processor, _) =>
        {
            Assert.That(() => processor.ProcessWithdrawals(BuildBlockWithWithdrawal(), Shanghai.Instance),
                Throws.TypeOf<InvalidBlockException>()
                    .With.InnerException.TypeOf<ContractCallFailedException>());
        });

    [Test]
    public void Should_not_report_withdrawals_system_call_execution_fault_as_invalid_block()
    {
        InvalidOperationException fault = new("state read failed");
        WithWithdrawalContract(Convert.FromHexString("60005400"), (processor, _) =>
        {
            Assert.That(() => processor.ProcessWithdrawals(BuildBlockWithWithdrawal(), Shanghai.Instance),
                Throws.TypeOf<AbiException>().With.InnerException.SameAs(fault));
        }, fault);
    }

    [Test]
    public void Should_allow_missing_withdrawal_contract([Values] bool emptyWithdrawals) =>
        WithWithdrawalContract([], (processor, _) =>
        {
            Block block = emptyWithdrawals
                ? Build.A.Block.WithNumber(123).WithWithdrawals([]).TestObject
                : BuildBlockWithWithdrawal();

            Assert.DoesNotThrow(() => processor.ProcessWithdrawals(block, Shanghai.Instance));
        });

    [Test]
    public void Should_execute_withdrawal_contract_with_empty_withdrawals() =>
        WithWithdrawalContract(Convert.FromHexString("600160005500"), (processor, state) =>
        {
            Block block = Build.A.Block.WithNumber(123).WithWithdrawals([]).TestObject;

            processor.ProcessWithdrawals(block, Shanghai.Instance);

            state.Get(new StorageCell(TestItem.AddressA, 0), out UInt256 value);
            Assert.That(value, Is.EqualTo(UInt256.One));
        });

    private static void WithWithdrawalContract(byte[] code, Action<AuraWithdrawalProcessor, IWorldState> test, Exception? stateReadFault = null)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Shanghai.Instance))
            .AddScoped<ITransactionProcessor, AuRaEthereumTransactionProcessor>();
        if (stateReadFault is not null)
            builder.AddDecorator<IWorldState>((_, state) => new FaultingWorldState(state, stateReadFault));

        using IContainer container = builder.Build();
        using ILifetimeScope lifetime = container.BeginLifetimeScope(scopeBuilder => scopeBuilder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = lifetime.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        if (code.Length != 0)
        {
            state.CreateAccount(TestItem.AddressA, 0, 1);
            state.InsertCode(TestItem.AddressA, code, Shanghai.Instance);
            state.Commit(Shanghai.Instance);
        }

        WithdrawalContract contract = new(lifetime.Resolve<ITransactionProcessor>(), AbiEncoder.Instance, TestItem.AddressA);
        test(new AuraWithdrawalProcessor(contract, LimboLogs.Instance), state);
    }

    private sealed class FaultingWorldState(IWorldState state, Exception fault) : WorldStateDecorator(state)
    {
        public override void Get(in StorageCell storageCell, out UInt256 value) => throw fault;
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
