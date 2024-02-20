// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Registration;
using Autofac.Core.Resolving.Pipeline;
using Autofac.Features.ResolveAnything;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Module = Autofac.Module;

namespace Nethermind.Runner.Modules;

/// <summary>
/// Basic common behaviour such as instrumentation and system related stuff. Should be completely compatible in all
/// situation and can be run in tests.
/// </summary>
public class BaseModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterSource(new AnyConcreteTypeNotAlreadyRegisteredSource());
        builder.RegisterSource(new ConfigRegistrationSource());
        LoggerMiddleware.Configure(builder);

        builder.RegisterType<EthereumJsonSerializer>()
            .As<IJsonSerializer>()
            .SingleInstance()
            .IfNotRegistered(typeof(IJsonSerializer));

        builder.RegisterSingleton<ChainSpecBasedSpecProvider, ISpecProvider>();
        builder.RegisterSingleton<CryptoRandom, ICryptoRandom>();

        builder.RegisterInstance(TimerFactory.Default)
            .As<ITimerFactory>();

        builder.RegisterInstance(Timestamper.Default)
            .As<ITimestamper>();

        builder.RegisterInstance(new FileSystem())
            .As<IFileSystem>();
    }

    /// <summary>
    /// Dynamically resolve IConfig<T>
    /// </summary>
    private class ConfigRegistrationSource : IRegistrationSource
    {
        public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
        {
            IServiceWithType swt = service as IServiceWithType;
            if (swt == null || !typeof(IConfig).IsAssignableFrom(swt.ServiceType))
            {
                // It's not a request for the base handler type, so skip it.
                return Enumerable.Empty<IComponentRegistration>();
            }

            // Dynamically resolve IConfig
            ComponentRegistration registration = new ComponentRegistration(
                Guid.NewGuid(),
                new DelegateActivator(swt.ServiceType, (c, p) =>
                {
                    IConfigProvider configProvider = c.Resolve<IConfigProvider>();
                    object config = typeof(IConfigProvider)
                        .GetMethod("GetConfig")
                        .MakeGenericMethod(swt.ServiceType)
                        .Invoke(configProvider, new object[] { });
                    return config;
                }),
                new RootScopeLifetime(),
                InstanceSharing.Shared,
                InstanceOwnership.OwnedByLifetimeScope,
                new[] { service },
                new Dictionary<string, object>());

            return new IComponentRegistration[] { registration };
        }

        public bool IsAdapterForIndividualComponents => false;
    }

    /// <summary>
    /// For automatically resolving ILogger
    /// </summary>
    private class LoggerMiddleware : IResolveMiddleware
    {
        private LoggerMiddleware()
        {
        }

        public PipelinePhase Phase => PipelinePhase.ParameterSelection;

        public void Execute(ResolveRequestContext context, Action<ResolveRequestContext> next)
        {
            // Add our parameters.
            context.ChangeParameters(context.Parameters.Union(
                new[]
                {
                    new ResolvedParameter(
                        (p, i) => p.ParameterType == typeof(ILogger),
                        (p, i) => i.Resolve<ILogManager>().GetClassLogger(p.Member.DeclaringType)
                    ),
                }));

            // Continue the resolve.
            next(context);
        }

        public static void Configure(ContainerBuilder builder)
        {
            LoggerMiddleware loggerMiddleware = new LoggerMiddleware();
            builder.ComponentRegistryBuilder.Registered += (senter, args) =>
            {
                args.ComponentRegistration.PipelineBuilding += (sender2, pipeline) =>
                {
                    pipeline.Use(loggerMiddleware);
                };
            };
        }
    }

}
