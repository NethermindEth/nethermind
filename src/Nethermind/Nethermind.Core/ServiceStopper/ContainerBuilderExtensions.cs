// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;

namespace Nethermind.Core.ServiceStopper;

public static class ContainerBuilderExtensions
{
    /// <summary>
    /// Add service stopper middleware that automatically add <see cref="IStoppableService"/> to <see cref="IServiceStopper"/>.
    /// Must be added before any other service registration.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static ContainerBuilder AddServiceStopper(this ContainerBuilder builder)
    {
        builder.AddSingleton<IServiceStopper, ServiceStopper>();
        builder.ComponentRegistryBuilder.Registered += (sender, args) =>
        {
            args.ComponentRegistration.PipelineBuilding += (sender2, pipeline) =>
            {
                pipeline.Use(ServiceStopperMiddleware.Instance);
            };
        };

        return builder;
    }
}
