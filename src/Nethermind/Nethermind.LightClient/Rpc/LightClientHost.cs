// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Nethermind.JsonRpc.Hosting;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Rpc;

internal static class LightClientHost
{
    internal static WebApplicationBuilder CreateBuilder(string[] args)
    {
        using ConfigurationManager hostConfiguration = new();
        hostConfiguration.AddEnvironmentVariables("ASPNETCORE_").AddEnvironmentVariables("DOTNET_").AddCommandLine(args);
        WebApplicationBuilder builder = JsonRpcHosting.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = hostConfiguration[HostDefaults.ApplicationKey],
            EnvironmentName = hostConfiguration[HostDefaults.EnvironmentKey],
            ContentRootPath = hostConfiguration[HostDefaults.ContentRootKey],
            WebRootPath = hostConfiguration[WebHostDefaults.WebRootKey],
        });
        builder.Configuration
            .AddEnvironmentVariables("ASPNETCORE_")
            .AddEnvironmentVariables("DOTNET_")
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
        builder.WebHost.ConfigureKestrel(static (context, options) =>
            options.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: true));
        builder.Services.AddResponseCompression();
        builder.Host.UseDefaultServiceProvider(static (context, options) =>
        {
            bool development = context.HostingEnvironment.IsDevelopment();
            options.ValidateScopes = development;
            options.ValidateOnBuild = development;
        });
        return builder;
    }

    internal static string ReadRpcUrls(IConfiguration configuration)
    {
        string? urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (urls is null) return "http://127.0.0.1:8545";
        if (urls.Length != 0) return urls;
        List<string> listeners = [];
        AddPorts(WebHostDefaults.HttpPortsKey, "http");
        AddPorts(WebHostDefaults.HttpsPortsKey, "https");
        return listeners.Count == 0 ? "http://localhost:5000" : string.Join(';', listeners);

        void AddPorts(string key, string scheme)
        {
            foreach (string port in (configuration[key] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                listeners.Add($"{scheme}://*:{port}");
        }
    }

    internal static bool TryPrepareDataDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using FileStream probe = new(Path.Combine(directory, $".light-client-write-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            probe.Flush();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Cannot write light-client data directory '{directory}' as user '{Environment.UserName}': {exception.Message} " +
                "Check directory ownership and write permissions. Docker volumes must be writable by UID 1654.";
            return false;
        }
    }

    internal static void ConfigureNetworkBuffers()
    {
        Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", "4096");
        Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", "9");
        Environment.SetEnvironmentVariable("io.netty.allocator.numHeapArenas", "2");
        Environment.SetEnvironmentVariable("io.netty.allocator.numDirectArenas", "2");
        InitializeNetworkBuffers();
    }

    /// <remarks>Keeps DotNetty static initialization after the process settings.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeNetworkBuffers()
    {
        NethermindBuffers.Default = PooledByteBufferAllocator.Default;
        NethermindBuffers.RlpxAllocator = PooledByteBufferAllocator.Default;
    }

    internal static (int Beacon, int Quic, int Execution) ReadPorts(IConfiguration configuration)
    {
        int beacon = ReadPort("beacon-port", 9050);
        int quic = ReadPort("beacon-quic-port", 9051, allowDisabled: true);
        int execution = ReadPort("execution-port", 30307);
        if (beacon == execution || quic != 0 && (quic == beacon || quic == execution))
            throw new ArgumentException("Beacon, beacon QUIC and execution ports must not overlap.");
        return (beacon, quic, execution);

        int ReadPort(string option, int defaultPort, bool allowDisabled = false)
        {
            int port = configuration.GetValue(option, defaultPort);
            if (port < (allowDisabled ? 0 : 1) || port > 65535)
                throw new ArgumentOutOfRangeException(option, port, "Port must be between 1 and 65535; beacon QUIC also accepts 0 to disable it.");
            return port;
        }
    }

    internal static void ConfigureRequestPolicy(IApplicationBuilder app, string rpcUrls)
    {
        bool loopbackOnly = true;
        if (rpcUrls.Length == 0)
        {
            string[] portKeys = [WebHostDefaults.HttpPortsKey, WebHostDefaults.HttpsPortsKey];
            foreach (string key in portKeys)
                if (app.ApplicationServices.GetRequiredService<IConfiguration>()[key]?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0)
                    loopbackOnly = false;
        }
        foreach (string value in rpcUrls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            BindingAddress address = BindingAddress.Parse(value);
            if (!IsLoopbackHost(address.Host)) loopbackOnly = false;
        }
        app.Use(async (context, next) =>
        {
            if (loopbackOnly && (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out Uri? host) || !IsLoopbackHost(host.Host)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string requestOrigin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(requestOrigin) &&
                !string.Equals(requestOrigin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase) &&
                !IsBrowserExtensionOrigin(requestOrigin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.UseCors(static policy => policy.SetIsOriginAllowed(IsBrowserExtensionOrigin).WithMethods("GET", "POST").AllowAnyHeader());
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Length > ".localhost".Length && host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);

    private static bool IsBrowserExtensionOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? url) ||
            !string.Equals(origin, $"{url.Scheme}://{url.Host}", StringComparison.OrdinalIgnoreCase)) return false;
        return url.Scheme switch
        {
            "chrome-extension" => url.Host.Length == 32 && url.Host.AsSpan().IndexOfAnyExceptInRange('a', 'p') < 0,
            "moz-extension" => Guid.TryParseExact(url.Host, "D", out _),
            _ => false,
        };
    }

    internal static async Task RunAsync(IHost app, Func<CancellationToken, Task> synchronize, ILogger? logger = null)
    {
        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        await app.StartAsync(lifetime.ApplicationStopping);
        if (logger is not null)
            foreach (string url in app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)
                logger.LogInformation("Light-client RPC and status listening at {Url}", url);
        Task server = app.WaitForShutdownAsync();
        Task? sync = null;
        try
        {
            sync = SynchronizeAsync();
            await Task.WhenAny(server, sync);
        }
        finally
        {
            lifetime.StopApplication();
            await Task.WhenAll(server, sync ?? Task.CompletedTask);
        }

        async Task SynchronizeAsync()
        {
            try { await synchronize(lifetime.ApplicationStopping); }
            catch (OperationCanceledException exception) when (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                throw new InvalidOperationException("Light-client startup or synchronization was canceled before application shutdown.", exception);
            }
        }
    }
}
