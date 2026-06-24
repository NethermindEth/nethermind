// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.Tracing;
using Nethermind.OpcodeTracing.Plugin.Tracing;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Autofac module for registering opcode tracing services. The configured tracing mode decides the wiring:
/// RealTime attaches a live block tracer to the main processor, while the Retrospective modes analyse
/// historical blocks via <see cref="StartOpcodeRetrospectiveTracing"/>.
/// </summary>
public class OpcodeTracingModule(IOpcodeTracingConfig config) : Module
{
    /// <summary>
    /// Loads services into the dependency injection container.
    /// </summary>
    /// <param name="builder">The container builder.</param>
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton(config)
            .AddSingleton<OpcodeTraceRecorder>();

        TracingMode mode = Enum.TryParse(config.Mode, ignoreCase: true, out TracingMode parsed)
            ? parsed
            : TracingMode.RealTime;

        if (mode == TracingMode.RealTime)
        {
            builder.AddSingleton<IMainProcessingModule, OpcodeRealTimeModule>();
        }
        else
        {
            builder.AddStep(typeof(StartOpcodeRetrospectiveTracing));
        }
    }

    /// <summary>
    /// Contributes the live opcode block tracer to the main block processor. Only loaded for RealTime mode.
    /// </summary>
    private class OpcodeRealTimeModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddSingleton<IBlockTracer>(ctx =>
                {
                    OpcodeTraceRecorder recorder = ctx.Resolve<OpcodeTraceRecorder>();
                    return recorder.Prepare() ? recorder.AttachRealTime() : NullBlockTracer.Instance;
                });
    }
}
