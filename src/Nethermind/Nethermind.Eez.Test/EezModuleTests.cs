// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using Autofac;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Network;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Sequencer;
using Nethermind.Merge.Plugin;
using NSubstitute;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.KeyStore;
using Nethermind.Specs.Forks;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NetworkForkId = Nethermind.Network.ForkId;
using EnrForkId = Nethermind.Network.Enr.ForkId;
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
            .AddModule(new EezModule(new EezConfig(), EezChainRules.Rollup0))
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
            .AddModule(new EezModule(config, EezChainRules.Rollup0))
            .Build();

        Assert.That(container.Resolve<EezFollower>(), Is.Not.Null, "the follower and everything it drives resolve");
        using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
        Assert.That(session, Is.Not.Null, "a derived block session opens its own processing scope with the strict executor");
    }

    [Test]
    public async Task Resolve_PeerSync_IsOptIn([Values] bool enabled)
    {
        EezConfig config = OverlayConfig(enabled);
        await using IContainer container = OverlayContainer(config);
        IUnsafeHeadSource source = container.Resolve<IUnsafeHeadSource>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.IsRegistered<EezPeerNetwork>(), Is.EqualTo(enabled));
            Assert.That(source.GetType(), Is.EqualTo(enabled ? typeof(SequencerPeerHeadSource) : typeof(NullUnsafeHeadSource)));
            Assert.That(container.Resolve<EezDriver>(), Is.Not.Null);
        }
    }

    [Test]
    public async Task Peer_driver_advances_between_L1_polls_only_when_enabled([Values] bool enabled)
    {
        IEezL2Engine engine = Substitute.For<IEezL2Engine>();
        await using IContainer container = OverlayContainer(OverlayConfig(enabled), engine);
        DateTimeOffset next = container.Resolve<ITimestamper>().UtcNowOffset.AddSeconds(1);

        (DateTimeOffset poll, DateTimeOffset wake) = await container.Resolve<EezDriver>().Step(next, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(poll, Is.EqualTo(next));
            Assert.That(wake, Is.EqualTo(next));
        }
        engine.Received(enabled ? 1 : 0).EnsureSoleDriver();
    }

    [Test]
    public async Task Peer_ENR_has_a_signed_overlay_port_and_keeps_the_eth_fork()
    {
        EezConfig config = OverlayConfig(true);
        await using IContainer container = OverlayContainer(config);
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(genesis);
        tree.TryUpdateMainChain(genesis.Header, true, true);
        NodeRecord record = await container.Resolve<INodeRecordProvider>().GetCurrentAsync();
        byte[] port = record.GetObj<byte[]>("eez")!;
        RlpReader reader = new(port);
        int decoded = reader.DecodeInt();
        reader.CheckEnd();
        NodeRecordSigner verifier = new(container.Resolve<IEthereumEcdsa>());
        EezPeerNetwork network = container.Resolve<EezPeerNetwork>();
        NetworkForkId fork = container.Resolve<IForkInfo>().GetForkId(genesis.Number, genesis.Timestamp);
        EnrForkId advertised = record.GetValue<EnrForkId>(EnrContentKey.Eth)!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.EqualTo(config.SequencerP2PPort));
            Assert.That(verifier.Verify(record), Is.True);
            Assert.That(advertised.ForkHash, Is.EqualTo(fork.HashBytes));
            Assert.That(advertised.Next, Is.EqualTo(fork.Next));
            Assert.That(network.TryAddress(record, out _), Is.True);
        }
        record.SetEntry(new EthEntry(fork.HashBytes, fork.Next + 1));
        Assert.That(network.TryAddress(record, out _), Is.False);
    }

    private static EezConfig OverlayConfig(bool enabled) => new()
    {
        FollowerEnabled = true,
        PeerSyncEnabled = enabled,
        L1RpcUrl = "http://127.0.0.1:8545",
        RegistryAddress = TestItem.AddressA.ToString(),
        SequencerP2PSigner = TestItem.AddressB.ToString(),
        RollupId = 1
    };

    private static IContainer OverlayContainer(EezConfig config, IEezL2Engine? engine = null)
    {
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IEezConfig>(config)
            .AddSingleton(resolver)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddModule(new EezModule(config, EezChainRules.Rollup0));
        if (engine is not null) builder.AddSingleton(engine);
        return builder.Build();
    }

    [Test]
    public void Resolve_SequencerEnabled_BuildsEverythingThatProducesProvesAndPosts()
    {
        using IContainer container = SequencerContainer(Result.Success);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<EezDriver>().Sequences, Is.True, "the driver runs the sequencer after the follower");
            Assert.That(container.Resolve<ISyncSlotComposer>(), Is.Not.Null, "the composer, the quorum, the registration reader and the poster resolve");
            Assert.That(container.Resolve<ILiveBlockProducer>(), Is.Not.Null, "the Live block producer opens its producer environment");
            Assert.That(container.Resolve<AttestationQuorum>().ProofSystems, Has.Length.EqualTo(1), "one attester per configured prover");
        }
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
            .AddModule(new EezModule(config, EezChainRules.Rollup0))
            .Build();
    }

    [Test]
    public void Resolve_TransactionProcessors_AreEezProcessorsOnEveryConstructionPath()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scope.Resolve<ITransactionProcessor>(), Is.InstanceOf<EezTransactionProcessor>(),
            "block processing, RPC and prewarming resolve the scoped processor");
            Assert.That(_scope.Resolve<ITransactionProcessorFactory>(), Is.InstanceOf<EezTransactionProcessorFactory>(),
                "block-level access list execution builds its processors through the factory");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scope.Resolve<IBlockValidator>(), Is.InstanceOf<EezBlockValidator>(), "block import rejects blobs and withdrawals");
            Assert.That(_scope.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey), Is.InstanceOf<EezSpecChangeTxValidator>(),
                "pool admission rejects system and blob transactions");
        }
    }

    [TestCase(false, TestName = "ScopedProcessor")]
    [TestCase(true, TestName = "OptionsForBlockAccessListExecution")]
    public void Resolve_ExecutionRequestsProcessor_SkipsCodelessRequestContracts(bool fromOptions)
    {
        IExecutionRequestsProcessor processor = fromOptions
            ? new ExecutionRequestsProcessor(_scope.Resolve<ITransactionProcessor>(), _scope.Resolve<ExecutionRequestsOptions>())
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
