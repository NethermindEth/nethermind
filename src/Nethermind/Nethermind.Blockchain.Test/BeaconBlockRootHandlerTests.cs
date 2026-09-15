// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class BeaconBlockRootHandlerTests
{
    private static readonly IReleaseSpec[] BeaconRootSpecs = [Cancun.Instance, Osaka.Instance, Amsterdam.Instance];
    private static readonly Hash256 BeaconRoot = new(new string('1', 64));

    private BeaconBlockRootHandler _beaconBlockRootHandler;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _worldState;

    [SetUp]
    public void Setup()
    {
        _worldState = Substitute.For<IWorldState>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _beaconBlockRootHandler = new BeaconBlockRootHandler(_transactionProcessor, _worldState);
    }

    [Test]
    public void Test_BeaconRootsAccessList_IsBeaconBlockRootAvailableFalse()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Shanghai.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_HeaderIsGenesis()
    {
        BlockHeader header = Build.A.BlockHeader.WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_ParentBeaconBlockRootIsNull()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountNotExist()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(false);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountExists()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);
        (_, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance, includeStorageCells: true);

        Assert.That(accessList, Is.Not.Null);
        Assert.That(accessList.Count.AddressesCount, Is.EqualTo(1));
        Assert.That(accessList.Count.StorageKeysCount, Is.EqualTo(2));
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountExists_IncludeStorageCellsIsFalse()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);
        (_, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance, false);

        Assert.That(accessList, Is.Not.Null);
        Assert.That(accessList.Count.AddressesCount, Is.EqualTo(1));
        Assert.That(accessList.Count.StorageKeysCount, Is.EqualTo(0));
    }

    [Test]
    public void Test_StoreBeaconRoot_AccessListIsNull()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        _beaconBlockRootHandler.StoreBeaconRoot(block, Cancun.Instance, NullTxTracer.Instance);

        _transactionProcessor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public void StoreBeaconRoot_constructs_transaction_for_current_fork(
        [ValueSource(nameof(BeaconRootSpecs))] IReleaseSpec spec)
    {
        Block block = Build.A.Block.WithNumber(1).WithParentBeaconBlockRoot(BeaconRoot).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);
        Transaction? transaction = null;
        _transactionProcessor.When(p => p.Process(Arg.Any<Transaction>(), NullTxTracer.Instance, ExecutionOptions.Commit))
            .Do(call => transaction = call.Arg<Transaction>());

        _beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);

        Assert.That(transaction, Is.Not.Null);
        Assert.That(transaction.AccessList, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction, Is.TypeOf(spec.IsEip8037Enabled ? typeof(SystemCall) : typeof(Transaction)));
            Assert.That(transaction.GasLimit, Is.EqualTo(spec.IsEip8037Enabled ? 31_566_720UL : 30_000_000UL));
            Assert.That(transaction.SenderAddress, Is.EqualTo(Address.SystemUser));
            Assert.That(transaction.To, Is.EqualTo(Eip4788Constants.BeaconRootsAddress));
            Assert.That(transaction.Data.ToArray(), Is.EqualTo(BeaconRoot.Bytes.ToArray()));
            Assert.That(transaction.Value, Is.EqualTo(UInt256.Zero));
            Assert.That(transaction.GasPrice, Is.EqualTo(UInt256.Zero));
            Assert.That(transaction.AccessList, Is.EqualTo(new AccessList.Builder().AddAddress(Eip4788Constants.BeaconRootsAddress).Build()));
            Assert.That(transaction.Hash, Is.EqualTo(transaction.CalculateHash()));
        }
        _transactionProcessor.Received(1).Execute(transaction, NullTxTracer.Instance);
    }

    [Test]
    public void StoreBeaconRoot_executes_with_fork_gas_and_excludes_block_counters(
        [ValueSource(nameof(BeaconRootSpecs))] IReleaseSpec spec)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(spec))
            .AddDecorator<IVirtualMachine, GasTrackingVirtualMachine>()
            .Build();
        using ILifetimeScope lifetime = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = lifetime.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, 1);
        state.InsertCode(Eip4788Constants.BeaconRootsAddress, Eip4788TestConstants.Code, spec);
        state.CreateAccount(TestItem.AddressA, 1.Ether);
        state.CreateAccount(TestItem.AddressB, 1);
        state.Commit(spec);

        Block block = Build.A.Block.WithNumber(1).WithTimestamp(12345).WithGasLimit(1_000_000)
            .WithParentBeaconBlockRoot(BeaconRoot).TestObject;
        ITransactionProcessor processor = lifetime.Resolve<ITransactionProcessor>();
        processor.SetBlockExecutionContext(block.Header);
        GasTrackingVirtualMachine vm = (GasTrackingVirtualMachine)lifetime.Resolve<IVirtualMachine>();
        CallOutputTracer tracer = new();

        lifetime.Resolve<IBeaconBlockRootHandler>().StoreBeaconRoot(block, spec, tracer);

        state.Get(new StorageCell(Eip4788Constants.BeaconRootsAddress, 4154), out UInt256 timestamp);
        state.Get(new StorageCell(Eip4788Constants.BeaconRootsAddress, 12345), out UInt256 root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            // Legacy execution deducts 21,000 base + 512 calldata + 2,400 access-list gas.
            Assert.That(vm.Before.Value, Is.EqualTo(spec.IsEip8037Enabled ? 30_000_000UL : 29_976_088UL));
            Assert.That(vm.Before.StateReservoir, Is.EqualTo(spec.IsEip8037Enabled ? 1_566_720L : 0L));
            Assert.That(vm.Before.StateGasUsed, Is.Zero);
            Assert.That(vm.After.StateReservoir, Is.EqualTo(spec.IsEip8037Enabled ? 1_370_880L : 0L));
            Assert.That(vm.After.StateGasUsed, Is.EqualTo(spec.IsEip8037Enabled ? 195_840L : 0L));
            Assert.That(vm.After.StateGasSpill, Is.Zero);
            Assert.That(timestamp, Is.EqualTo((UInt256)12345));
            Assert.That(root, Is.EqualTo(new UInt256(BeaconRoot.Bytes, isBigEndian: true)));
            Assert.That(block.GasUsed, Is.Zero);
            Assert.That(state.AccountExists(Address.SystemUser), Is.False);
        }

        Transaction userTransaction = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Assert.That(processor.Execute(userTransaction, NullTxTracer.Instance), Is.EqualTo(TransactionResult.Ok));
        Assert.That(block.GasUsed, Is.EqualTo(userTransaction.BlockGasUsed).And.GreaterThan(0),
            "system execution and state gas must not leak into the next user transaction's block counters");
    }

    private sealed class GasTrackingVirtualMachine(IVirtualMachine inner) : IVirtualMachine
    {
        public EthereumGasPolicy Before { get; private set; }
        public EthereumGasPolicy After { get; private set; }

        public TransactionSubstate ExecuteTransaction<TTracingInst>(VmState<EthereumGasPolicy> state, IWorldState worldState, ITxTracer tracer)
            where TTracingInst : struct, IFlag
        {
            Before = state.Gas;
            TransactionSubstate result = inner.ExecuteTransaction<TTracingInst>(state, worldState, tracer);
            After = state.Gas;
            return result;
        }

        public ref readonly BlockExecutionContext BlockExecutionContext => ref inner.BlockExecutionContext;
        public ref readonly TxExecutionContext TxExecutionContext => ref inner.TxExecutionContext;
        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);
        public void SetTxExecutionContext(in TxExecutionContext context) => inner.SetTxExecutionContext(in context);
        public int OpCodeCount => inner.OpCodeCount;
        public void FlushMetricsCounters() => inner.FlushMetricsCounters();
    }
}
