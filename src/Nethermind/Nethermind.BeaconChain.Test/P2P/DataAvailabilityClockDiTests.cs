// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class DataAvailabilityClockDiTests
{
    [Test]
    public void Range_sync_the_importer_factory_and_the_p2p_host_get_the_one_registered_clock()
    {
        List<(Type Consumer, Type Dependency, object? Instance)> handedOut = [];
        ContainerBuilder builder = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia);
        using IContainer container = BeaconChainTestContainer.BuildTrackingDependencies(builder, handedOut, typeof(SlotClock));

        container.Resolve<RangeSync>();
        container.Resolve<IBlockImporterFactory>();
        container.Resolve<BeaconP2P>();
        SlotClock registered = container.Resolve<SlotClock>();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(handedOut.Select(static h => h.Consumer), Is.SupersetOf(new[] { typeof(RangeSync), typeof(BlockImporterFactory), typeof(BeaconP2P) }));
        Assert.That(handedOut.Select(static h => h.Instance), Is.All.SameAs(registered));
    }

    [Test]
    public void Range_sync_and_the_importer_factory_do_not_resolve_without_a_registered_clock([Values] bool registerClock)
    {
        ContainerBuilder builder = new();
        builder.RegisterType<RangeSync>();
        builder.RegisterType<BlockImporterFactory>();
        builder.RegisterInstance(BeaconChainSpec.Mainnet);
        builder.RegisterInstance(Substitute.For<IBeaconSyncPeerPool>());
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(new DataColumnSidecarPool());
        builder.RegisterInstance(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()));
        builder.RegisterInstance(new PubkeyCache());
        builder.RegisterInstance(Substitute.For<IEngineDriver>());
        builder.RegisterInstance(new BeaconChainConfig()).As<IBeaconChainConfig>();
        if (registerClock) builder.RegisterInstance(new SlotClock(BeaconChainSpec.Mainnet, Timestamper.Default));
        using IContainer container = builder.Build();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => container.Resolve<RangeSync>(), registerClock ? Throws.Nothing : Throws.InstanceOf<DependencyResolutionException>());
        Assert.That(() => container.Resolve<BlockImporterFactory>(), registerClock ? Throws.Nothing : Throws.InstanceOf<DependencyResolutionException>());
    }
}
