// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nethermind.JsonRpc.Hosting;

/// <summary>Configures the shared Kestrel transport and request hosting services.</summary>
public static class JsonRpcHosting
{
    /// <summary>Creates a lean JSON-RPC application builder with the shared transport memory pool.</summary>
    /// <param name="options">The application identity and host options.</param>
    /// <returns>A builder whose request middleware and application services remain to be configured.</returns>
    /// <remarks>Configuration defaults are not added; callers supply their existing configuration sources.</remarks>
    public static WebApplicationBuilder CreateBuilder(WebApplicationOptions options)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(options);
        builder.Services.AddSingleton<DiagnosticListener>(NullDiagnosticListener.Instance);
        builder.Services.AddSingleton<DiagnosticSource>(NullDiagnosticListener.Instance);
        // Explicitly build from UseKestrelCore rather than UseKestrel to
        // not add additional transports that we don't use e.g. msquic as that
        // adds a lot of additional idle threads to the process.
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.Services.AddJsonRpcTransportMemoryPool();
        return builder;
    }

    /// <summary>Registers the shared transport pool in place of Kestrel's default pool.</summary>
    /// <param name="services">The host services.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddJsonRpcTransportMemoryPool(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IMemoryPoolFactory<byte>, TransportMemoryPoolFactory>());
        return services;
    }
}
