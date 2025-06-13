// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Autofac.Core.Lifetime;
using Autofac.Core.Resolving.Pipeline;

namespace Nethermind.Core.ServiceStopper;

public class ServiceStopperMiddleware : IResolveMiddleware
{
    public static ServiceStopperMiddleware Instance { get; } = new ServiceStopperMiddleware();

    public void Execute(ResolveRequestContext context, Action<ResolveRequestContext> next)
    {
        next(context);

        if (context.Registration.Ownership != InstanceOwnership.OwnedByLifetimeScope) return; // Container owned only
        if (context.Registration.Lifetime != RootScopeLifetime.Instance) return; // Only if it is registered at top level
        if (context.Registration.Sharing != InstanceSharing.Shared) return; // Only if it is a singleton

        if (context.Instance is IStoppableService stoppable)
        {
            context.ActivationScope.Resolve<IServiceStopper>().AddStoppable(stoppable);
        }
    }

    public PipelinePhase Phase => PipelinePhase.Activation;
}
