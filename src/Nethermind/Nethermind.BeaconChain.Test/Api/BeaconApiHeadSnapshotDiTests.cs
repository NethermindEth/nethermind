// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Sync;
using Nethermind.Config;
using Nethermind.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconApiHeadSnapshotDiTests
{
    [Test]
    public void Orchestrator_and_the_beacon_api_share_the_one_registered_head_snapshot_holder()
    {
        List<(Type Consumer, Type Dependency, object? Instance)> resolved = [];
        ContainerBuilder builder = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia)
            .AddModule(new BeaconApiModule())
            .AddSingleton<IBeaconApiConfig>(new BeaconApiConfig())
            .AddSingleton(Substitute.For<IProcessExitSource>());
        using IContainer container = BeaconChainTestContainer.BuildTrackingDependencies(builder, resolved, typeof(HeadSnapshotHolder));

        container.Resolve<BeaconSyncOrchestrator>();
        container.Resolve<BeaconApiHost>();
        HeadSnapshotHolder registered = container.Resolve<HeadSnapshotHolder>();

        object?[] orchestratorHolders = [.. resolved.Where(static r => r.Consumer == typeof(BeaconSyncOrchestrator)).Select(static r => r.Instance)];
        object?[] apiHolders = [.. resolved.Where(static r => r.Consumer == typeof(BeaconApiHost)).Select(static r => r.Instance)];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(orchestratorHolders, Has.Length.EqualTo(1), "the orchestrator asks the container for the holder");
        Assert.That(orchestratorHolders, Is.All.SameAs(registered), "the orchestrator publishes to the registered holder");
        Assert.That(apiHolders, Has.Length.EqualTo(1), "the API asks the container for the holder");
        Assert.That(apiHolders, Is.All.SameAs(registered), "the API reads the registered holder");
    }
}
