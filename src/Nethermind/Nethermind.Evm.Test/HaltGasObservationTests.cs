// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Verifies <see cref="EthereumGasPolicy.UnspentGasBeforeHalt"/>: the execution gas a halted frame had left, recorded
/// only on failing paths, including the fixed-cost charges the untraced dispatch runs outside the frame's policy.
/// </summary>
/// <param name="traced">Whether the transaction runs traced, so every charge runs on the frame's policy.</param>
[TestFixture(false)]
[TestFixture(true)]
public sealed class HaltGasObservationTests(bool traced)
{
    private static readonly Address Child = new("0x00000000000000000000000000000000000000c7");
    private const ulong ChildGas = 10;

    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _worldState = null!;
    private ISpecProvider _specProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Prague.Instance))
            .AddScoped<IVirtualMachine, HaltObservingVirtualMachine>()
            .Build();
        _scope = _container.BeginLifetimeScope(builder =>
            builder.AddSingleton<IWorldStateScopeProvider>(_container.Resolve<IWorldStateManager>().GlobalWorldState));
        _worldState = _scope.Resolve<IWorldState>();
        _stateScope = _worldState.BeginScope(IWorldState.PreGenesis);
        _specProvider = _scope.Resolve<ISpecProvider>();
    }

    [TearDown]
    public void TearDown()
    {
        _stateScope.Dispose();
        _scope.Dispose();
        _container.Dispose();
    }

    /// <param name="childCode">What the child runs on <see cref="ChildGas"/> gas.</param>
    /// <param name="unspent">The gas the child had left when it halted.</param>
    [TestCase("6001600160016001", EvmExceptionType.OutOfGas, 1UL, TestName = "Fixed cost out of gas")]
    [TestCase("600162010000" + "52", EvmExceptionType.OutOfGas, 1UL, TestName = "Memory expansion out of gas after the static cost")]
    [TestCase("56", EvmExceptionType.StackUnderflow, 2UL, TestName = "Fixed-cost opcode failing its stack check after its charge")]
    [TestCase("6001" + "0c", EvmExceptionType.BadInstruction, 7UL, TestName = "Undefined opcode, a halt that clears no gas")]
    public void Halted_child_reports_the_gas_it_had_left(string childCode, EvmExceptionType halt, ulong unspent)
    {
        HaltObservingVirtualMachine machine = Execute(Bytes.FromHexString(childCode));

        Assert.That(machine.Halts, Is.EqualTo(new[] { (halt, unspent) }));
    }

    [Test]
    public void A_new_child_budget_starts_without_the_previous_halt()
    {
        HaltObservingVirtualMachine machine = Execute(Bytes.FromHexString("6001600160016001"), secondCall: true);

        Assert.That(machine.Halts, Is.EqualTo(new[] { (EvmExceptionType.OutOfGas, 1UL), (EvmExceptionType.OutOfGas, 1UL) }));
    }

    [Test]
    public void A_completed_child_records_no_halt()
    {
        HaltObservingVirtualMachine machine = Execute(Bytes.FromHexString("600160015000"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(machine.Halts, Is.Empty);
            Assert.That(machine.CompletedWithoutHaltRecord, Is.EqualTo(1));
        }
    }

    [Test]
    public void Successful_charges_leave_the_record_untouched()
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100_000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EthereumGasPolicy.UpdateGas(ref gas, 3), Is.True);
            Assert.That(EthereumGasPolicy.TryConsumeCallValueTransfer(ref gas), Is.True);
            Assert.That(EthereumGasPolicy.TryReserveChildGas(ref gas, (UInt256)1_000, Prague.Instance, out _), Is.True);
            Assert.That(gas.UnspentGasBeforeHalt, Is.Zero);
        }
    }

    [TestCase(0UL, 1UL)]
    [TestCase(97UL, 100UL)]
    public void Failed_charge_records_the_unspent_gas_without_refunding_it(ulong remaining, ulong cost)
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(remaining);

        Assert.That(EthereumGasPolicy.UpdateGas(ref gas, cost), Is.False);
        EthereumGasPolicy.ClearExecutionGas(ref gas);
        EthereumGasPolicy parent = EthereumGasPolicy.FromULong(10);
        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in gas);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.Zero);
            Assert.That(gas.UnspentGasBeforeHalt, Is.EqualTo(remaining), "a later clearing keeps what the failed charge recorded");
            Assert.That(EthereumGasPolicy.GetRemainingGas(in parent), Is.EqualTo(10UL));
            Assert.That(EthereumGasPolicy.CreateChildFrameGas(ref gas, 100).UnspentGasBeforeHalt, Is.Zero);
        }
    }

    [Test]
    public void The_record_fits_the_policy_in_seven_words() =>
        Assert.That(Unsafe.SizeOf<EthereumGasPolicy>(), Is.EqualTo(56));

    private HaltObservingVirtualMachine Execute(byte[] childCode, bool secondCall = false)
    {
        IReleaseSpec genesisSpec = _specProvider.GenesisSpec;
        _worldState.CreateAccount(TestItem.AddressA, 100.Ether);
        _worldState.CreateAccount(Child, 0);
        _worldState.InsertCode(Child, childCode, genesisSpec);
        Prepare parent = Prepare.EvmCode.CALL(ChildGas, Child, 0, 0, 0, 0, 0).Op(Instruction.POP);
        if (secondCall) parent = parent.CALL(ChildGas, Child, 0, 0, 0, 0, 0).Op(Instruction.POP);
        _worldState.CreateAccount(TestItem.AddressB, 0);
        _worldState.InsertCode(TestItem.AddressB, parent.Done, genesisSpec);
        _worldState.Commit(genesisSpec);
        _worldState.CommitTree(0);

        Transaction transaction = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(1UL)
            .To(TestItem.AddressB)
            .SignedAndResolved(new EthereumEcdsa(_specProvider.ChainId), TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTimestamp(0).WithTransactions([transaction]).WithGasLimit(8_000_000)
            .WithBeneficiary(TestItem.AddressD).WithBlobGasUsed(0).WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakG).WithSlotNumber(0).TestObject;

        ITxTracer tracer = traced ? new TestAllTracerWithOutput() : NullTxTracer.Instance;
        TransactionResult result = _scope.Resolve<ITransactionProcessor>()
            .Execute(transaction, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);
        Assert.That(result.TransactionExecuted, Is.True);
        return (HaltObservingVirtualMachine)_scope.Resolve<IVirtualMachine>();
    }

    /// <summary>Records, for each child that halts, the gas the child had left, as a chain metering halted frames would.</summary>
    private sealed class HaltObservingVirtualMachine(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager)
        : VirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine
    {
        public List<(EvmExceptionType Halt, ulong Unspent)> Halts { get; } = [];
        public int CompletedWithoutHaltRecord { get; private set; }

        protected override void OnChildFrameCompleted(VmState<EthereumGasPolicy> childState, EvmExceptionType exceptionType,
            bool shouldRevert, ReadOnlyMemory<byte> output)
        {
            if (exceptionType is not (EvmExceptionType.None or EvmExceptionType.Revert))
                Halts.Add((exceptionType, childState.Gas.Value + childState.Gas.UnspentGasBeforeHalt));
            else if (childState.Gas.UnspentGasBeforeHalt == 0)
                CompletedWithoutHaltRecord++;
        }
    }
}
