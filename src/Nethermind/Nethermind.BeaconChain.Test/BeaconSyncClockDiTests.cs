// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core.Resolving.Pipeline;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

/// <summary>
/// The orchestrator paces range sync against the wall clock: if range sync held a different clock, it would
/// chase a target the orchestrator never sees, so both must resolve the one registered <see cref="SlotClock"/>.
/// </summary>
public class BeaconSyncClockDiTests
{
    [Test]
    public void Orchestrator_and_the_range_sync_it_drives_share_the_one_registered_clock()
    {
        List<(Type Consumer, Type Dependency, object? Instance)> resolved = [];
        Stack<Type> activating = new();
        ContainerBuilder builder = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia);
        builder.ComponentRegistryBuilder.Registered += (_, registered) => registered.ComponentRegistration.PipelineBuilding += (_, pipeline) =>
            pipeline.Use(PipelinePhase.RegistrationPipelineStart, (context, next) =>
            {
                activating.Push(context.Registration.Activator.LimitType);
                try
                {
                    next(context);
                }
                finally
                {
                    activating.Pop();
                }
            });
        using IContainer container = builder.Build();
        container.ResolveOperationBeginning += (_, operation) => operation.ResolveOperation.ResolveRequestBeginning += (_, request) =>
        {
            Type dependency = request.RequestContext.Registration.Activator.LimitType;
            if ((dependency == typeof(SlotClock) || dependency == typeof(RangeSync)) && activating.TryPeek(out Type? consumer))
            {
                request.RequestContext.RequestCompleting += (_, completing) => resolved.Add((consumer, dependency, completing.RequestContext.Instance));
            }
        };

        container.Resolve<BeaconSyncOrchestrator>();
        SlotClock registeredClock = container.Resolve<SlotClock>();
        RangeSync registeredRangeSync = container.Resolve<RangeSync>();

        using (Assert.EnterMultipleScope())
        {
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
}
