// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;

namespace Nethermind.BeaconChain.Test;

public class BeaconSyncClockDiTests
{
    [Test]
    public void Orchestrator_and_the_range_sync_it_drives_share_the_one_registered_clock()
    {
        List<(Type Consumer, Type Dependency, object? Instance)> resolved = [];
        ContainerBuilder builder = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia);
        using IContainer container = BeaconChainTestContainer.BuildTrackingDependencies(builder, resolved, typeof(SlotClock), typeof(RangeSync));

        container.Resolve<BeaconSyncOrchestrator>();
        SlotClock registeredClock = container.Resolve<SlotClock>();
        RangeSync registeredRangeSync = container.Resolve<RangeSync>();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        object?[] orchestratorClock = [.. resolved.Where(static r => r.Consumer == typeof(BeaconSyncOrchestrator) && r.Dependency == typeof(SlotClock)).Select(static r => r.Instance)];
        object?[] rangeSyncClock = [.. resolved.Where(static r => r.Consumer == typeof(RangeSync) && r.Dependency == typeof(SlotClock)).Select(static r => r.Instance)];
        object?[] orchestratorRangeSync = [.. resolved.Where(static r => r.Consumer == typeof(BeaconSyncOrchestrator) && r.Dependency == typeof(RangeSync)).Select(static r => r.Instance)];

        Assert.That(orchestratorClock, Has.Length.EqualTo(1), "the orchestrator asks the container for a clock");
        Assert.That(orchestratorClock, Is.All.SameAs(registeredClock), "orchestrator clock");
        Assert.That(rangeSyncClock, Has.Length.EqualTo(1), "range sync asks the container for a clock");
        Assert.That(rangeSyncClock, Is.All.SameAs(registeredClock), "range sync clock");
        Assert.That(orchestratorRangeSync, Has.Length.EqualTo(1), "the orchestrator asks the container for range sync");
        Assert.That(orchestratorRangeSync, Is.All.SameAs(registeredRangeSync), "the orchestrator drives the container's range sync");
    }
}
