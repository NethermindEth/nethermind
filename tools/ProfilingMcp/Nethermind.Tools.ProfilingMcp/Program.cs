// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Tools.ProfilingMcp;
using Nethermind.Tools.ProfilingMcp.EventPipe;

if (args is [ContainerSidecar.IdleOption])
{
    await ContainerSidecar.IdleAsync();
    return;
}

ContainerSidecar.LinkTargetTempFolder();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging must go to stderr.
builder.Logging.AddConsole(static options => options.LogToStandardErrorThreshold = LogLevel.Trace);

WorkspacePaths paths = WorkspacePaths.FromEnvironment();
ContainerRouter router = new();
builder.Services
    .AddSingleton(paths)
    .AddSingleton(router)
    .AddSingleton<NetTraceReader>()
    .AddMcpServer(static options => options.ServerInstructions = ServerInstructions.Text)
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(ToolErrors.ReportExceptionMessages);
        if (paths.Sidecar is null)
        {
            filters.AddListToolsFilter(ContainerRouter.AddTargetArguments);
            filters.AddCallToolFilter(router.Route);
        }
    });

await builder.Build().RunAsync();
await router.DisposeAsync();
