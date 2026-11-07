// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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

/// <summary>Verifies policy-specific accounting around persistent storage transitions.</summary>
[NonParallelizable]
public sealed class StorageTransitionAccountingTests
{
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _worldState = null!;
    private ISpecProvider _specProvider = null!;
    private ITransactionProcessor _processor = null!;
    private ProbeState _probe = null!;
    private ProbeState? _previousProbe;

    [SetUp]
    public void SetUp()
    {
        _probe = new ProbeState();
        _previousProbe = ProbeGasPolicy.SetCurrent(_probe);

        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithBlobGasUsed(0).TestObject,
        };
        IConfigProvider configProvider = TestStateBackend.ApplyDefaultBackend(new ConfigProvider());

        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, nameof(StorageTransitionAccountingTests)))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance))
            .AddScoped<IVirtualMachine<ProbeGasPolicy>, VirtualMachine<ProbeGasPolicy>>()
            .AddScoped<ITransactionProcessor, TransactionProcessor<ProbeGasPolicy>>()
            .Build();

        _scope = _container.BeginLifetimeScope(builder =>
            builder.AddSingleton<IWorldStateScopeProvider>(_container.Resolve<IWorldStateManager>().GlobalWorldState));
        _worldState = _scope.Resolve<IWorldState>();
        _stateScope = _worldState.BeginScope(IWorldState.PreGenesis);
        _specProvider = _scope.Resolve<ISpecProvider>();
        _processor = _scope.Resolve<ITransactionProcessor>();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            _stateScope?.Dispose();
            _scope?.Dispose();
            _container?.Dispose();
        }
        finally
        {
            ProbeGasPolicy.SetCurrent(_previousProbe);
        }
    }

    [TestCase(0, 1, 1)]
    [TestCase(7, 7, 0)]
    [TestCase(7, 9, 1)]
    public void Storage_transition_hook_runs_only_for_changed_values(byte initialValue, byte newValue, int expectedCalls)
    {
        (Block block, Transaction transaction) = PrepareTransaction(
            Prepare.EvmCode.SSTORE(0, [newValue]).Done,
            new UInt256(initialValue));

        TransactionResult result = Execute(transaction, block);

        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        _worldState.Get(in cell, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.None));
            Assert.That(_probe.Calls, Is.EqualTo(expectedCalls), "the production SSTORE seam must dispatch only changed values");
            if (expectedCalls is 1)
            {
                Assert.That(_probe.LastCell, Is.EqualTo(cell));
                Assert.That(_probe.LastCurrent, Is.EqualTo(new UInt256(initialValue)));
                Assert.That(_probe.LastNew, Is.EqualTo(new UInt256(newValue)));
            }
            Assert.That(value, Is.EqualTo(new UInt256(newValue)));
        }
    }

    [Test]
    public void Policy_error_stops_before_primary_storage_write()
    {
        _probe.Result = EvmExceptionType.OutOfGas;
        _probe.WriteBookkeepingCell = true;
        (Block block, Transaction transaction) = PrepareTransaction(
            Prepare.EvmCode.SSTORE(0, [1]).Done);

        TransactionResult result = Execute(transaction, block);

        StorageCell primaryCell = new(TestItem.AddressB, UInt256.Zero);
        StorageCell bookkeepingCell = new(TestItem.AddressB, UInt256.One);
        _worldState.Get(in primaryCell, out UInt256 value);
        _worldState.Get(in bookkeepingCell, out UInt256 bookkeepingValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.OutOfGas));
            Assert.That(_probe.Calls, Is.EqualTo(1));
            Assert.That(value, Is.EqualTo(UInt256.Zero));
            Assert.That(bookkeepingValue, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Policy_bookkeeping_cell_is_reverted_with_primary_storage()
    {
        _probe.WriteBookkeepingCell = true;
        (Block block, Transaction transaction) = PrepareTransaction(
            Prepare.EvmCode.SSTORE(0, [1]).REVERT(0, 0).Done);

        TransactionResult result = Execute(transaction, block);

        StorageCell primaryCell = new(TestItem.AddressB, UInt256.Zero);
        StorageCell bookkeepingCell = new(TestItem.AddressB, UInt256.One);
        _worldState.Get(in primaryCell, out UInt256 primaryValue);
        _worldState.Get(in bookkeepingCell, out UInt256 bookkeepingValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.Revert));
            Assert.That(_probe.Calls, Is.EqualTo(1));
            Assert.That(primaryValue, Is.EqualTo(UInt256.Zero));
            Assert.That(bookkeepingValue, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Policy_exception_is_visible_to_transaction_processor()
    {
        InvalidOperationException expected = new("policy accounting failed");
        _probe.Exception = expected;
        (Block block, Transaction transaction) = PrepareTransaction(
            Prepare.EvmCode.SSTORE(0, [1]).Done);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => Execute(transaction, block))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(_probe.Calls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Static_sstore_rejects_before_policy_transition_hook()
    {
        byte[] staticCall = Prepare.EvmCode.StaticCall(TestItem.AddressC, 50_000).Op(Instruction.POP).Done;
        byte[] staticTarget = Prepare.EvmCode.SSTORE(0, [1]).Done;
        (Block block, Transaction transaction) = PrepareTransaction(staticCall, auxiliaryCode: staticTarget);

        TestAllTracerWithOutput tracer = new();
        TransactionResult result = Execute(transaction, block, tracer);

        StorageCell cell = new(TestItem.AddressC, UInt256.Zero);
        _worldState.Get(in cell, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.None));
            Assert.That(_probe.Calls, Is.Zero);
            Assert.That(value, Is.EqualTo(UInt256.Zero));
            Assert.That(tracer.ReportedActionErrors, Does.Contain(EvmExceptionType.StaticCallViolation));
        }
    }

    [Test]
    public void Policy_bookkeeping_cell_is_reverted_with_a_reverting_child()
    {
        _probe.WriteBookkeepingCell = true;
        byte[] childCode = Prepare.EvmCode.SSTORE(0, [1]).REVERT(0, 0).Done;
        byte[] callerCode = Prepare.EvmCode.Call(TestItem.AddressC, 50_000).Op(Instruction.POP).Done;
        (Block block, Transaction transaction) = PrepareTransaction(callerCode, auxiliaryCode: childCode);

        TransactionResult result = Execute(transaction, block);

        StorageCell primaryCell = new(TestItem.AddressC, UInt256.Zero);
        StorageCell bookkeepingCell = new(TestItem.AddressC, UInt256.One);
        _worldState.Get(in primaryCell, out UInt256 primaryValue);
        _worldState.Get(in bookkeepingCell, out UInt256 bookkeepingValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.None));
            Assert.That(_probe.Calls, Is.EqualTo(1));
            Assert.That(primaryValue, Is.EqualTo(UInt256.Zero));
            Assert.That(bookkeepingValue, Is.EqualTo(UInt256.Zero));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Policy_storage_refund_selectors_are_used(bool reversal)
    {
        const long clearRefund = 1_234;
        const long reversalRefund = 2_345;
        _probe.StorageClearRefund = clearRefund;
        _probe.StorageReversalRefund = reversalRefund;

        byte[] code = reversal
            ? Prepare.EvmCode.SSTORE(0, [1]).SSTORE(0, [0]).Done
            : Prepare.EvmCode.SSTORE(0, [0]).Done;
        UInt256 initialValue = reversal ? UInt256.Zero : UInt256.One;
        (Block block, Transaction transaction) = PrepareTransaction(code, initialValue);
        TestAllTracerWithOutput tracer = new();

        TransactionResult result = Execute(transaction, block, tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.None));
            Assert.That(tracer.Refund, Is.EqualTo(reversal ? reversalRefund : clearRefund));
        }
    }

    [Test]
    public void Ethereum_policy_transition_hook_is_a_noop_by_default()
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100_000);
        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        VirtualMachine<EthereumGasPolicy> vm = (VirtualMachine<EthereumGasPolicy>)_scope.Resolve<IVirtualMachine>();

        EvmExceptionType result = InvokeStorageTransitionAccounting(
            ref gas, vm, in cell, in UInt256.Zero, in UInt256.One);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(100_000));
            Assert.That(GetStorageClearRefund<EthereumGasPolicy>(_specProvider.GenesisSpec), Is.EqualTo((long)_specProvider.GenesisSpec.GasCosts.SClearRefund));
            Assert.That(GetStorageReversalRefund<EthereumGasPolicy>(_specProvider.GenesisSpec, true),
                Is.EqualTo((long)_specProvider.GenesisSpec.GasCosts.RefundFromReversal(true)));
            Assert.That(GetStorageReversalRefund<EthereumGasPolicy>(_specProvider.GenesisSpec, false),
                Is.EqualTo((long)_specProvider.GenesisSpec.GasCosts.RefundFromReversal(false)));
        }
    }

    private static EvmExceptionType InvokeStorageTransitionAccounting<TGasPolicy>(
        ref TGasPolicy gas,
        VirtualMachine<TGasPolicy> vm,
        in StorageCell cell,
        in UInt256 currentValue,
        in UInt256 newValue)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.ApplyStorageTransitionAccounting(ref gas, vm, in cell, in currentValue, in newValue);

    private static long GetStorageClearRefund<TGasPolicy>(IReleaseSpec spec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> => TGasPolicy.GetStorageClearRefund(spec);

    private static long GetStorageReversalRefund<TGasPolicy>(IReleaseSpec spec, bool originalIsZero)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> => TGasPolicy.GetStorageReversalRefund(spec, originalIsZero);

    private TransactionResult Execute(Transaction transaction, Block block, ITxTracer? tracer = null) =>
        _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)),
            tracer ?? NullTxTracer.Instance);

    private (Block Block, Transaction Transaction) PrepareTransaction(
        byte[] code,
        UInt256? initialValue = null,
        byte[]? auxiliaryCode = null)
    {
        Address sender = TestItem.AddressA;
        Address recipient = TestItem.AddressB;
        IReleaseSpec genesisSpec = _specProvider.GenesisSpec;

        if (!_worldState.AccountExists(sender))
            _worldState.CreateAccount(sender, 100.Ether);
        else
            _worldState.AddToBalance(sender, 100.Ether, genesisSpec);

        if (!_worldState.AccountExists(recipient))
            _worldState.CreateAccount(recipient, 0);
        _worldState.InsertCode(recipient, code, genesisSpec);
        if (initialValue is UInt256 value)
        {
            StorageCell cell = new(recipient, UInt256.Zero);
            _worldState.Set(in cell, in value);
        }
        if (auxiliaryCode is not null)
        {
            if (!_worldState.AccountExists(TestItem.AddressC))
                _worldState.CreateAccount(TestItem.AddressC, 0);
            _worldState.InsertCode(TestItem.AddressC, auxiliaryCode, genesisSpec);
        }
        _worldState.Commit(genesisSpec);
        _worldState.CommitTree(0);

        EthereumEcdsa ecdsa = new(_specProvider.ChainId);
        Transaction transaction = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(1UL)
            .WithValue(0)
            .WithNonce(_worldState.GetNonce(sender))
            .To(recipient)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTimestamp(0)
            .WithTransactions([transaction])
            .WithGasLimit(8_000_000)
            .WithBeneficiary(TestItem.AddressD)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakG)
            .WithSlotNumber(0)
            .TestObject;
        return (block, transaction);
    }

    private sealed class ProbeState
    {
        public int Calls { get; private set; }
        public bool HasCall { get; private set; }
        public EvmExceptionType Result { get; set; }
        public bool WriteBookkeepingCell { get; set; }
        public long? StorageClearRefund { get; set; }
        public long? StorageReversalRefund { get; set; }
        public Exception? Exception { get; set; }
        public StorageCell LastCell { get; private set; }
        public UInt256 LastCurrent { get; private set; }
        public UInt256 LastNew { get; private set; }

        public void Record(in StorageCell cell, in UInt256 currentValue, in UInt256 newValue)
        {
            Calls++;
            HasCall = true;
            LastCell = cell;
            LastCurrent = currentValue;
            LastNew = newValue;
        }
    }

    private struct ProbeGasPolicy : IGasPolicy<ProbeGasPolicy>
    {
        private static readonly AsyncLocal<ProbeState?> Current = new();
        private EthereumGasPolicy _inner;

        private ProbeGasPolicy(EthereumGasPolicy inner) => _inner = inner;

        public static ProbeState? SetCurrent(ProbeState? state)
        {
            ProbeState? previous = Current.Value;
            Current.Value = state;
            return previous;
        }

        public static EvmExceptionType ApplyStorageTransitionAccounting(
            ref ProbeGasPolicy gas,
            VirtualMachine<ProbeGasPolicy> vm,
            in StorageCell cell,
            in UInt256 currentValue,
            in UInt256 newValue)
        {
            _ = gas;
            ProbeState? state = Current.Value;
            if (state is null)
                return EvmExceptionType.None;

            state.Record(in cell, in currentValue, in newValue);
            if (state.Exception is not null)
                throw state.Exception;

            if (state.WriteBookkeepingCell)
            {
                StorageCell bookkeepingCell = new(cell.Address, UInt256.One);
                vm.WorldState.Get(in bookkeepingCell, out UInt256 bookkeepingCurrent);
                UInt256 bookkeepingValue = new(9);
                vm.WorldState.Set(in bookkeepingCell, in bookkeepingValue, in bookkeepingCurrent);
            }

            return state.Result;
        }

        public static ProbeGasPolicy FromULong(ulong value) => new(EthereumGasPolicy.FromULong(value));

        public static long GetStorageClearRefund(IReleaseSpec spec) =>
            Current.Value?.StorageClearRefund ?? (long)spec.GasCosts.SClearRefund;

        public static long GetStorageReversalRefund(IReleaseSpec spec, bool originalIsZero) =>
            Current.Value?.StorageReversalRefund ?? (long)spec.GasCosts.RefundFromReversal(originalIsZero);

        public static ulong GetRemainingGas(in ProbeGasPolicy gas) => EthereumGasPolicy.GetRemainingGas(in gas._inner);

        public static void ClearExecutionGas(ref ProbeGasPolicy gas) => EthereumGasPolicy.ClearExecutionGas(ref gas._inner);

        public static bool TryConsumeSelfDestructGas(ref ProbeGasPolicy gas) => EthereumGasPolicy.TryConsumeSelfDestructGas(ref gas._inner);

        public static void Refund(ref ProbeGasPolicy gas, in ProbeGasPolicy childGas) =>
            EthereumGasPolicy.Refund(ref gas._inner, in childGas._inner);

        public static bool TryConsumeAccountAccessGasWithDelegation(
            ref ProbeGasPolicy gas,
            IReleaseSpec spec,
            ref readonly StackAccessTracker accessTracker,
            bool isTracingAccess,
            Address address,
            Address? delegated) =>
            EthereumGasPolicy.TryConsumeAccountAccessGasWithDelegation(
                ref gas._inner, spec, in accessTracker, isTracingAccess, address, delegated);

        public static bool TryConsumeAccountAccessGas(
            ref ProbeGasPolicy gas,
            IReleaseSpec spec,
            ref readonly StackAccessTracker accessTracker,
            bool isTracingAccess,
            Address address,
            AccountAccessKind kind = AccountAccessKind.Default) =>
            EthereumGasPolicy.TryConsumeAccountAccessGas(
                ref gas._inner, spec, in accessTracker, isTracingAccess, address, kind);

        public static bool TryConsumeStorageAccessGas(
            ref ProbeGasPolicy gas,
            ref readonly StackAccessTracker accessTracker,
            bool isTracingAccess,
            in StorageCell storageCell,
            StorageAccessType storageAccessType,
            IReleaseSpec spec) =>
            EthereumGasPolicy.TryConsumeStorageAccessGas(
                ref gas._inner, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec);

        public static bool UpdateMemoryCost(
            ref ProbeGasPolicy gas,
            in UInt256 position,
            in UInt256 length,
            ref EvmPooledMemory memory) =>
            EthereumGasPolicy.UpdateMemoryCost(ref gas._inner, in position, in length, ref memory);

        public static bool UpdateGas(ref ProbeGasPolicy gas, ulong gasCost) =>
            EthereumGasPolicy.UpdateGas(ref gas._inner, gasCost);

        public static bool TryConsumeStateAndExecutionGas(ref ProbeGasPolicy gas, long stateGasCost, ulong executionGasCost) =>
            EthereumGasPolicy.TryConsumeStateAndExecutionGas(ref gas._inner, stateGasCost, executionGasCost);

        public static void UpdateGasUp(ref ProbeGasPolicy gas, ulong refund) =>
            EthereumGasPolicy.UpdateGasUp(ref gas._inner, refund);

        public static bool TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref ProbeGasPolicy gas, IReleaseSpec spec)
            where TEip8037 : struct, IFlag
            where TIsSlotCreation : struct, IFlag =>
            EthereumGasPolicy.TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref gas._inner, spec);

        public static bool TryConsumeCallValueTransfer(ref ProbeGasPolicy gas) =>
            EthereumGasPolicy.TryConsumeCallValueTransfer(ref gas._inner);

        public static bool TryConsumeCallValueTransferEip2780(ref ProbeGasPolicy gas) =>
            EthereumGasPolicy.TryConsumeCallValueTransferEip2780(ref gas._inner);

        public static bool TryConsumeNewAccountCreation<TEip8037>(ref ProbeGasPolicy gas)
            where TEip8037 : struct, IFlag =>
            EthereumGasPolicy.TryConsumeNewAccountCreation<TEip8037>(ref gas._inner);

        public static bool TryConsumeLogEmission(ref ProbeGasPolicy gas, ulong topicCount, ulong dataSize) =>
            EthereumGasPolicy.TryConsumeLogEmission(ref gas._inner, topicCount, dataSize);

        public static ProbeGasPolicy Max(in ProbeGasPolicy a, in ProbeGasPolicy b) =>
            new(EthereumGasPolicy.Max(in a._inner, in b._inner));

        public static IntrinsicGas<ProbeGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
        {
            IntrinsicGas<EthereumGasPolicy> inner = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, blockGasLimit);
            return new(new(inner.Standard), new(inner.FloorGas));
        }

        public static bool TryCreateAvailableFromIntrinsic(
            ulong gasLimit,
            in ProbeGasPolicy intrinsicGas,
            IReleaseSpec spec,
            out ProbeGasPolicy available,
            bool capExecutionGas = true)
        {
            bool result = EthereumGasPolicy.TryCreateAvailableFromIntrinsic(
                gasLimit, in intrinsicGas._inner, spec, out EthereumGasPolicy inner, capExecutionGas);
            available = new(inner);
            return result;
        }

        public static bool TryConsumeDataCopyGas(ref ProbeGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words) =>
            EthereumGasPolicy.TryConsumeDataCopyGas(ref gas._inner, spec, isExternalCode, words);
    }
}
