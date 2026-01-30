// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.OpcodeTracing.Plugin.Output;
using Nethermind.OpcodeTracing.Plugin.Tracing;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Autofac module for registering opcode tracing services.
/// </summary>
public class OpcodeTracingModule : Module
{
    /// <summary>
    /// Loads services into the dependency injection container.
    /// </summary>
    /// <param name="builder">The container builder.</param>
    protected override void Load(ContainerBuilder builder)
    {
        // Register configuration
        builder
            .RegisterType<OpcodeTracingConfig>()
            .As<IOpcodeTracingConfig>()
            .SingleInstance();

        // Register core services
        builder
            .RegisterType<OpcodeCounter>()
            .AsSelf()
            .SingleInstance();

        builder
            .RegisterType<TraceOutputWriter>()
            .AsSelf()
            .SingleInstance();

        // Register trace recorder
        builder
            .RegisterType<OpcodeTraceRecorder>()
            .AsSelf()
            .SingleInstance();
    }
}
