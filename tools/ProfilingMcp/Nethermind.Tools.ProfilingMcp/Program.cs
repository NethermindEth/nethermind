// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Tools.ProfilingMcp;
using Nethermind.Tools.ProfilingMcp.DotTrace;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging must go to stderr.
builder.Logging.AddConsole(static options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddSingleton(WorkspacePaths.FromEnvironment())
    .AddSingleton<DotTraceLocator>()
    .AddSingleton<DotTraceSessionManager>()
    .AddSingleton<Reporter>()
    .AddMcpServer(static options => options.ServerInstructions = ServerInstructions.Text)
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(static filters => filters.AddCallToolFilter(ToolErrors.ReportExceptionMessages));

await builder.Build().RunAsync();
