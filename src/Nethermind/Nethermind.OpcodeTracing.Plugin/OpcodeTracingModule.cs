// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
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
        builder
            .AddSingleton<IOpcodeTracingConfig, OpcodeTracingConfig>()
            .AddSingleton<OpcodeCounter>()
            .AddSingleton<TraceOutputWriter>()
            .AddSingleton<OpcodeTraceRecorder>();
    }
}
