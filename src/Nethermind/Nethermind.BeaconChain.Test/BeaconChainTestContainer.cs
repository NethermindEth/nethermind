// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac;
using Autofac.Core.Resolving.Pipeline;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Network;
using NSubstitute;

namespace Nethermind.BeaconChain.Test;

internal static class BeaconChainTestContainer
{
    internal static IContainer BuildTrackingDependencies(ContainerBuilder builder,
        List<(Type Consumer, Type Dependency, object? Instance)> resolved, params Type[] dependencies)
    {
        Stack<Type> activating = new();
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
        IContainer container = builder.Build();
        container.ResolveOperationBeginning += (_, operation) => operation.ResolveOperation.ResolveRequestBeginning += (_, request) =>
        {
            Type dependency = request.RequestContext.Registration.Activator.LimitType;
            if (dependencies.Contains(dependency) && activating.TryPeek(out Type? consumer))
                request.RequestContext.RequestCompleting += (_, completing) => resolved.Add((consumer, dependency, completing.RequestContext.Instance));
        };
        return container;
    }

    public static ContainerBuilder Builder(
        ulong chainId = BlockchainIds.Mainnet,
        ILogManager? logManager = null,
        IEngineRpcModule? engine = null,
        IBeaconChainConfig? config = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>(); // registered by NetworkModule in production
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>(); // registered by NethermindModule in production
        specProvider.ChainId.Returns(chainId);
        return new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton(config ?? new BeaconChainConfig())
            .AddSingleton(logManager ?? LimboLogs.Instance)
            .AddSingleton(engine ?? Substitute.For<IEngineRpcModule>()) // registered by MergePlugin in production
            .AddSingleton<ITimestamper>(Timestamper.Default) // registered by NethermindModule in production
            .AddSingleton(ipResolver)
            .AddSingleton(specProvider)
            .AddSingleton<IDbFactory, MemDbFactory>();
    }
}
