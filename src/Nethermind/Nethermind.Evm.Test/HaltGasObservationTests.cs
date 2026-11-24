// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Verifies that a chain that meters what a halted frame used can do so with a gas policy of its own, while
/// <see cref="EthereumGasPolicy"/> keeps no record of halts.
/// </summary>
/// <remarks>
/// The dispatch carries execution gas outside the frame's policy for <see cref="EthereumGasPolicy"/> only. Under any other
/// policy every charge runs on the frame's policy, so the policy sees each failed charge, fixed costs included.
/// </remarks>
public sealed class HaltGasObservationTests
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
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithBlobGasUsed(0).TestObject,
        };
        IConfigProvider configProvider = TestStateBackend.ApplyDefaultBackend(new ConfigProvider());

        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, nameof(HaltGasObservationTests)))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance))
            .AddScoped<IVirtualMachine<HaltObservingGasPolicy>, HaltObservingVirtualMachine>()
            .AddScoped<ITransactionProcessor, TransactionProcessor<HaltObservingGasPolicy>>()
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

        TransactionResult result = _scope.Resolve<ITransactionProcessor>()
            .Execute(transaction, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True);
        return (HaltObservingVirtualMachine)_scope.Resolve<IVirtualMachine<HaltObservingGasPolicy>>();
    }

    /// <summary>Records, for each child that halts, the gas the child had left when it halted.</summary>
    private sealed class HaltObservingVirtualMachine(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager)
        : VirtualMachine<HaltObservingGasPolicy>(blockHashProvider, specProvider, logManager)
    {
        public List<(EvmExceptionType Halt, ulong Unspent)> Halts { get; } = [];

        protected override void OnChildFrameCompleted(VmState<HaltObservingGasPolicy> childState, EvmExceptionType exceptionType,
            bool shouldRevert, ReadOnlyMemory<byte> output)
        {
            if (exceptionType is not (EvmExceptionType.None or EvmExceptionType.Revert))
                Halts.Add((exceptionType, HaltObservingGasPolicy.GetRemainingGas(in childState.Gas) + childState.Gas.UnspentGasBeforeHalt));
        }
    }

    /// <summary><see cref="EthereumGasPolicy"/> that also keeps the execution gas a failed charge or a clearing found.</summary>
    private struct HaltObservingGasPolicy : IGasPolicy<HaltObservingGasPolicy>
    {
        private EthereumGasPolicy _inner;

        /// <summary>The execution gas left when a charge failed or the gas was cleared; zero for a frame that did not halt.</summary>
        public ulong UnspentGasBeforeHalt;

        private HaltObservingGasPolicy(EthereumGasPolicy inner) => _inner = inner;

        /// <summary>Applies <paramref name="charged"/>, the result of a charge, keeping what it left unspent when it failed.</summary>
        private static bool Observe(ref HaltObservingGasPolicy gas, ulong before, bool charged)
        {
            if (!charged && before != 0) gas.UnspentGasBeforeHalt = before;
            return charged;
        }

        public static HaltObservingGasPolicy FromULong(ulong value) => new(EthereumGasPolicy.FromULong(value));

        public static ulong GetRemainingGas(in HaltObservingGasPolicy gas) => EthereumGasPolicy.GetRemainingGas(in gas._inner);

        public static void ClearExecutionGas(ref HaltObservingGasPolicy gas)
        {
            if (gas._inner.Value != 0) gas.UnspentGasBeforeHalt = gas._inner.Value;
            EthereumGasPolicy.ClearExecutionGas(ref gas._inner);
        }

        public static bool TryConsumeSelfDestructGas(ref HaltObservingGasPolicy gas) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeSelfDestructGas(ref gas._inner));

        public static void Refund(ref HaltObservingGasPolicy gas, in HaltObservingGasPolicy childGas) =>
            EthereumGasPolicy.Refund(ref gas._inner, in childGas._inner);

        public static bool TryConsumeAccountAccessGasWithDelegation(ref HaltObservingGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker accessTracker, Address address, Address? delegated) =>
            Observe(ref gas, gas._inner.Value,
                EthereumGasPolicy.TryConsumeAccountAccessGasWithDelegation(ref gas._inner, spec, in accessTracker, address, delegated));

        public static bool TryConsumeAccountAccessGas(ref HaltObservingGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker accessTracker, Address address, AccountAccessKind kind = AccountAccessKind.Default) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeAccountAccessGas(ref gas._inner, spec, in accessTracker, address, kind));

        public static bool TryConsumeStorageAccessGas(ref HaltObservingGasPolicy gas, ref readonly StackAccessTracker accessTracker,
            in StorageCell storageCell, StorageAccessType storageAccessType, IReleaseSpec spec) =>
            Observe(ref gas, gas._inner.Value,
                EthereumGasPolicy.TryConsumeStorageAccessGas(ref gas._inner, in accessTracker, in storageCell, storageAccessType, spec));

        public static bool UpdateMemoryCost(ref HaltObservingGasPolicy gas, in UInt256 position, in UInt256 length, ref EvmPooledMemory memory) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.UpdateMemoryCost(ref gas._inner, in position, in length, ref memory));

        public static bool UpdateGas(ref HaltObservingGasPolicy gas, ulong gasCost) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.UpdateGas(ref gas._inner, gasCost));

        public static bool TryConsumeStateAndExecutionGas(ref HaltObservingGasPolicy gas, long stateGasCost, ulong executionGasCost) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeStateAndExecutionGas(ref gas._inner, stateGasCost, executionGasCost));

        public static void UpdateGasUp(ref HaltObservingGasPolicy gas, ulong refund) => EthereumGasPolicy.UpdateGasUp(ref gas._inner, refund);

        public static bool TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref HaltObservingGasPolicy gas, IReleaseSpec spec)
            where TEip8037 : struct, IFlag
            where TIsSlotCreation : struct, IFlag =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref gas._inner, spec));

        public static bool TryConsumeCallValueTransfer(ref HaltObservingGasPolicy gas) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeCallValueTransfer(ref gas._inner));

        public static bool TryConsumeCallValueTransferEip2780(ref HaltObservingGasPolicy gas) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeCallValueTransferEip2780(ref gas._inner));

        public static bool TryConsumeNewAccountCreation<TEip8037>(ref HaltObservingGasPolicy gas)
            where TEip8037 : struct, IFlag =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeNewAccountCreation<TEip8037>(ref gas._inner));

        public static bool TryConsumeLogEmission(ref HaltObservingGasPolicy gas, ulong topicCount, ulong dataSize) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeLogEmission(ref gas._inner, topicCount, dataSize));

        public static HaltObservingGasPolicy Max(in HaltObservingGasPolicy a, in HaltObservingGasPolicy b) =>
            new(EthereumGasPolicy.Max(in a._inner, in b._inner));

        public static IntrinsicGas<HaltObservingGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
        {
            IntrinsicGas<EthereumGasPolicy> inner = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, blockGasLimit);
            return new(new(inner.Standard), new(inner.FloorGas));
        }

        public static bool TryCreateAvailableFromIntrinsic(ulong gasLimit, in HaltObservingGasPolicy intrinsicGas, IReleaseSpec spec,
            out HaltObservingGasPolicy available, bool capExecutionGas = true)
        {
            bool result = EthereumGasPolicy.TryCreateAvailableFromIntrinsic(gasLimit, in intrinsicGas._inner, spec, out EthereumGasPolicy inner, capExecutionGas);
            available = new(inner);
            return result;
        }

        public static bool TryConsumeDataCopyGas(ref HaltObservingGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words) =>
            Observe(ref gas, gas._inner.Value, EthereumGasPolicy.TryConsumeDataCopyGas(ref gas._inner, spec, isExternalCode, words));
    }
}
