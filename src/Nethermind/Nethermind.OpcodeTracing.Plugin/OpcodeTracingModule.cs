// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

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
        // OpcodeCounter, TraceOutputWriter and OpcodeTraceRecorder are constructed directly
        // by OpcodeTracingPlugin.Init — they are not resolved from the container, so we do not
        // register them here (registering + hand-rolling would leave dead entries behind).
        builder.AddSingleton<IOpcodeTracingConfig, OpcodeTracingConfig>();
    }
}
