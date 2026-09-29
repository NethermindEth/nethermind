// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security;
using Autofac;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Sequencer;
using Nethermind.Merge.Plugin;
using NSubstitute;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.KeyStore;
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
            .AddModule(new EezModule(new EezConfig()))
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
    public void Resolve_FollowerEnabled_BuildsTheFollowerAndItsExecutionScope()
    {
        EezConfig config = new()
        {
            FollowerEnabled = true,
            L1RpcUrl = "http://127.0.0.1:8545",
            L1ChainId = 3151908,
            RegistryAddress = "0x5fbdb2315678afecb367f032d93f642f64180aa3",
            RollupId = 1,
        };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IEezConfig>(config)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddModule(new EezModule(config))
            .Build();

        Assert.That(container.Resolve<EezFollower>(), Is.Not.Null, "the follower and everything it drives resolve");
        using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
        Assert.That(session, Is.Not.Null, "a derived block session opens its own processing scope with the strict executor");
    }

    [Test]
    public void Resolve_SequencerEnabled_BuildsEverythingThatProducesProvesAndPosts()
    {
        using IContainer container = SequencerContainer(Result.Success);

        Assert.That(container.Resolve<EezDriver>().Sequences, Is.True, "the driver runs the sequencer after the follower");
        Assert.That(container.Resolve<ISyncSlotComposer>(), Is.Not.Null, "the composer, the quorum, the registration reader and the poster resolve");
        Assert.That(container.Resolve<ILiveBlockProducer>(), Is.Not.Null, "the Live block producer opens its producer environment");
        Assert.That(container.Resolve<AttestationQuorum>().ProofSystems, Has.Length.EqualTo(1), "one attester per configured prover");
    }

    [Test]
    public void Resolve_PosterKeyTheKeystoreDoesNotOpen_RefusesToStart()
    {
        using IContainer container = SequencerContainer(Result.Fail("wrong password"));

        Exception e = Assert.Catch(() => container.Resolve<IPostBatchPoster>())!;

        Assert.That(e.GetBaseException(), Is.TypeOf<InvalidConfigurationException>(), "a sequencer that cannot sign its batches must not start");
    }

    private static IContainer SequencerContainer(Result opened)
    {
        string passwordFile = Path.GetTempFileName();
        File.WriteAllText(passwordFile, "poster");
        EezConfig config = new()
        {
            FollowerEnabled = true,
            SequencerEnabled = true,
            L1RpcUrl = "http://127.0.0.1:8545",
            L1ChainId = 3151908,
            RegistryAddress = "0x5fbdb2315678afecb367f032d93f642f64180aa3",
            RegistryDeployBlock = 1,
            RollupId = 2,
            Provers = ["http://127.0.0.1:50061=0x70997970c51812dc3a010c7d01b50e0d17dc79c8=0xe7f1725e7734ce288f8367e1bb143e90bb3f0512"],
            PosterAddress = TestItem.PrivateKeyA.Address.ToString(),
            PosterPasswordFile = passwordFile,
        };
        IKeyStore keyStore = Substitute.For<IKeyStore>();
        keyStore.GetProtectedKey(TestItem.PrivateKeyA.Address, Arg.Any<SecureString>())
            .Returns((opened ? new ProtectedPrivateKey(TestItem.PrivateKeyA, Path.GetTempPath()) : null, opened));
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IEezConfig>(config)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddSingleton(keyStore)
            .AddModule(new EezModule(config))
            .Build();
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
        _scope.Resolve<ITransactionProcessor>().SetBlockExecutionContext(new BlockExecutionContext(block.Header, Osaka.Instance));

        Assert.That(() => processor.ProcessExecutionRequests(block, _codelessState, [], Osaka.Instance), Throws.Nothing,
            "every construction path receives the EEZ options");
        Assert.That(block.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash),
            "codeless predeploys contribute no requests");
    }
}
