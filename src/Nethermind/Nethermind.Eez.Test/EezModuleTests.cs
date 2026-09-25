// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezModuleTests
{
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IWorldState _codelessState = null!;

    [SetUp]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new EezModule())
            .Build();
        _codelessState = TestWorldStateFactory.CreateForTest();
        _scope = _container.BeginLifetimeScope(processingScope => processingScope.AddSingleton(_codelessState));
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _container.Dispose();
    }

    [Test]
    public void Resolve_TransactionProcessors_AreEezProcessorsOnEveryConstructionPath()
    {
        Assert.That(_scope.Resolve<ITransactionProcessor>(), Is.InstanceOf<EezTransactionProcessor>(),
            "block processing, RPC and prewarming resolve the scoped processor");
        Assert.That(_scope.Resolve<ITransactionProcessorFactory>(), Is.InstanceOf<EezTransactionProcessorFactory>(),
            "block-level access list execution builds its processors through the factory");
    }

    [Test]
    public void Resolve_SpecProvider_ServesEezReleaseSpecs()
    {
        ISpecProvider specProvider = _scope.Resolve<ISpecProvider>();

        Assert.That(specProvider, Is.InstanceOf<EezSpecProvider>(), "every spec consumer sees the EEZ deposit contract default");
        Assert.That(specProvider.GenesisSpec, Is.InstanceOf<EezReleaseSpec>(), "the genesis spec is decorated too");
    }

    [Test]
    public void Resolve_Validators_AreTheEezRules()
    {
        Assert.That(_scope.Resolve<IBlockValidator>(), Is.InstanceOf<EezBlockValidator>(), "block import rejects blobs and withdrawals");
        Assert.That(_scope.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey), Is.InstanceOf<EezSpecChangeTxValidator>(),
            "pool admission rejects system and blob transactions");
    }

    [TestCase(false, TestName = "ScopedProcessor")]
    [TestCase(true, TestName = "FactoryForBlockAccessListExecution")]
    public void Resolve_ExecutionRequestsProcessor_SkipsCodelessRequestContracts(bool fromFactory)
    {
        IExecutionRequestsProcessor processor = fromFactory
            ? _scope.Resolve<IExecutionRequestsProcessorFactory>().Create(_scope.Resolve<ITransactionProcessor>())
            : _scope.Resolve<IExecutionRequestsProcessor>();
        using IDisposable stateScope = _codelessState.BeginScope(IWorldState.PreGenesis);
        Block block = Build.A.Block.WithNumber(1).TestObject;

        Assert.That(() => processor.ProcessExecutionRequests(block, _codelessState, [], Osaka.Instance), Throws.Nothing,
            "every construction path receives the EEZ options");
        Assert.That(block.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash),
            "codeless predeploys contribute no requests");
    }
}
