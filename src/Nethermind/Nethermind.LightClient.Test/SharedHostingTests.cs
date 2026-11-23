// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Hosting;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using ApplicationLifetime = Microsoft.Extensions.Hosting.Internal.ApplicationLifetime;
using NullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;
using SharedWebHost = Nethermind.JsonRpc.Hosting.WebHost;

namespace Nethermind.LightClient.Test;

[NonParallelizable]
public class SharedHostingTests
{
    private const string ChainIdRequest = """{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""";
    private const string ChromeOrigin = "chrome-extension://nkbihfbeogaeaoehlefnkodbefgpgknn";

    [Test]
    public async Task Builder_preserves_scope_validation_in_development(
        [Values("Development", "Production")] string environment)
    {
        WebApplicationBuilder builder = LightClientHost.CreateBuilder(["--environment", environment]);
        builder.Services.AddScoped<DisposalProbe>();
        await using WebApplication application = builder.Build();

        if (environment == Environments.Development)
            Assert.Throws<InvalidOperationException>(() => application.Services.GetRequiredService<DisposalProbe>());
        else
            Assert.That(application.Services.GetRequiredService<DisposalProbe>(), Is.Not.Null);
    }

    [Test]
    public async Task Builder_selects_host_configuration_before_loading_appsettings([Values] bool commandLineOverrides)
    {
        string root = Path.Combine(Path.GetTempPath(), $"light-client-host-config-{Guid.NewGuid():N}");
        string aspnetRoot = Path.Combine(root, "aspnet");
        string dotnetRoot = Path.Combine(root, "dotnet");
        string commandRoot = Path.Combine(root, "command");
        foreach (string directory in new[] { aspnetRoot, dotnetRoot, commandRoot }) Directory.CreateDirectory(directory);
        string environmentName = commandLineOverrides ? "Review" : "Staging";
        string contentRoot = commandLineOverrides ? commandRoot : dotnetRoot;
        try
        {
            File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"),
                """{"baseValue":"base","jsonLayer":"base","LC_HOST_TEST_VALUE":"base"}""");
            File.WriteAllText(Path.Combine(contentRoot, $"appsettings.{environmentName}.json"),
                """{"jsonLayer":"environment","LC_HOST_TEST_VALUE":"json"}""");
            using ScopedEnvironment environment = new(
                ("ASPNETCORE_ENVIRONMENT", "Development"), ("DOTNET_ENVIRONMENT", "Staging"),
                ("ASPNETCORE_CONTENTROOT", aspnetRoot), ("DOTNET_CONTENTROOT", dotnetRoot),
                ("LC_HOST_TEST_VALUE", "environment"));
            string[] arguments = commandLineOverrides
                ? ["--environment", environmentName, "--contentRoot", contentRoot, "--LC_HOST_TEST_VALUE", "command"]
                : [];
            WebApplicationBuilder builder = LightClientHost.CreateBuilder(arguments);
            await using WebApplication application = builder.Build();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(builder.Environment.EnvironmentName, Is.EqualTo(environmentName));
                Assert.That(builder.Environment.ContentRootPath, Is.EqualTo(contentRoot));
                Assert.That(builder.Configuration["baseValue"], Is.EqualTo("base"));
                Assert.That(builder.Configuration["jsonLayer"], Is.EqualTo("environment"));
                Assert.That(builder.Configuration["LC_HOST_TEST_VALUE"], Is.EqualTo(commandLineOverrides ? "command" : "environment"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestCase(null, null, null, "http://127.0.0.1:8545")]
    [TestCase(null, "8080", "9443", "http://127.0.0.1:8545")]
    [TestCase("", null, null, "http://localhost:5000")]
    [TestCase("", "", "", "http://localhost:5000")]
    [TestCase("", "8000; 8001 ;", null, "http://*:8000;http://*:8001")]
    [TestCase("", null, "9443", "https://*:9443")]
    [TestCase("", "8000;8001", "9443;9444", "http://*:8000;http://*:8001;https://*:9443;https://*:9444")]
    [TestCase("http://127.0.0.1:8545;http://[::1]:8545", "8080", "9443", "http://127.0.0.1:8545;http://[::1]:8545")]
    public void Effective_urls_preserve_default_binding_and_expand_ports_only_for_empty_urls(
        string? urls, string? httpPorts, string? httpsPorts, string expected)
    {
        using ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = urls,
            [WebHostDefaults.HttpPortsKey] = httpPorts,
            [WebHostDefaults.HttpsPortsKey] = httpsPorts,
        });

        Assert.That(LightClientHost.ReadRpcUrls(configuration), Is.EqualTo(expected));
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Shared_listener_serves_metadata_status_and_metrics_before_sync(
        [Values("http://127.0.0.1:0", "http://[::1]:0")] string url, CancellationToken token)
    {
        if (url.Contains("[::1]", StringComparison.Ordinal) && !Socket.OSSupportsIPv6)
            Assert.Ignore("IPv6 is unavailable on this host.");
        await using TestHost runtime = await TestHost.CreateAsync(url, token);
        await runtime.Host.StartAsync(token);
        using HttpClient client = new() { BaseAddress = runtime.Address };
        using StringContent request = new(ChainIdRequest, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/", request, token);
        string responseBody = await response.Content.ReadAsStringAsync(token);
        using JsonDocument result = JsonDocument.Parse(responseBody);
        using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("/status", token));
        string metrics = await client.GetStringAsync("/metrics", token);
        IMemoryPoolFactory<byte> factory = runtime.Host.Services.GetRequiredService<IMemoryPoolFactory<byte>>();
        using MemoryPool<byte> pool = factory.Create();
        using IMemoryOwner<byte> block = pool.Rent(1);
        string? chainId = result.RootElement.GetProperty("result").GetString();
        string? startupStage = status.RootElement.GetProperty("startupStage").GetString();
        bool rpcInitialized = status.RootElement.GetProperty("rpcInitialized").GetBoolean();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(chainId, Is.EqualTo("0x1"));
            Assert.That(startupStage, Is.EqualTo("checkpoint"));
            Assert.That(rpcInitialized, Is.False);
            Assert.That(metrics, Does.Contain("nethermind_light_client_startup_stage"));
            Assert.That(metrics, Does.Contain("stage=\"checkpoint\""));
            Assert.That(metrics, Does.Contain("method=\"eth_chainId\""));
            Assert.That(block.Memory.Length, Is.EqualTo(64 * 1024));
            Assert.That(runtime.Lifetime.ApplicationStarted.IsCancellationRequested, Is.True);
            Assert.That(runtime.Host.Services.GetRequiredService<IHostApplicationLifetime>(), Is.SameAs(runtime.Lifetime));
        }
    }

    [TestCase(null, null, HttpStatusCode.OK)]
    [TestCase(ChromeOrigin, null, HttpStatusCode.OK)]
    [TestCase("moz-extension://12345678-1234-1234-1234-123456789abc", null, HttpStatusCode.OK)]
    [TestCase("http://attacker.example", null, HttpStatusCode.Forbidden)]
    [TestCase("http://attacker.example", "attacker.example", HttpStatusCode.Forbidden)]
    [TestCase("null", null, HttpStatusCode.Forbidden)]
    [CancelAfter(15_000)]
    public async Task Shared_listener_preserves_wallet_origin_and_loopback_policy(
        string? origin, string? host, HttpStatusCode expected, CancellationToken token)
    {
        await using TestHost runtime = await TestHost.CreateAsync("http://127.0.0.1:0", token);
        await runtime.Host.StartAsync(token);
        using HttpClient client = new() { BaseAddress = runtime.Address };
        foreach (string path in new[] { "/", "/status", "/metrics" })
        {
            using HttpRequestMessage request = new(path == "/" ? HttpMethod.Post : HttpMethod.Get, path);
            if (path == "/") request.Content = new StringContent(ChainIdRequest, Encoding.UTF8, "application/json");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            using HttpResponseMessage response = await client.SendAsync(request, token);
            Assert.That(response.StatusCode, Is.EqualTo(expected), path);
        }
    }

    [TestCase("wallet.local", HttpStatusCode.OK)]
    [TestCase("attacker.example", HttpStatusCode.Forbidden)]
    [TestCase("127.0.0.1", HttpStatusCode.OK)]
    [CancelAfter(15_000)]
    public async Task Shared_listener_applies_configured_allowed_hosts_on_public_binding(
        string host, HttpStatusCode expected, CancellationToken token)
    {
        await using TestHost runtime = await TestHost.CreateAsync("http://0.0.0.0:0", token, ["wallet.local"]);
        await runtime.Host.StartAsync(token);
        using HttpClient client = new() { BaseAddress = runtime.Address };
        using HttpRequestMessage request = new(HttpMethod.Get, "/status");
        request.Headers.Host = host;
        request.Headers.Add("Origin", ChromeOrigin);

        using HttpResponseMessage response = await client.SendAsync(request, token);

        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(ChromeOrigin, HttpStatusCode.NoContent)]
    [TestCase("http://attacker.example", HttpStatusCode.Forbidden)]
    [CancelAfter(15_000)]
    public async Task Shared_listener_preserves_wallet_cors_preflight(string origin, HttpStatusCode expected, CancellationToken token)
    {
        await using TestHost runtime = await TestHost.CreateAsync("http://127.0.0.1:0", token);
        await runtime.Host.StartAsync(token);
        using HttpClient client = new() { BaseAddress = runtime.Address };
        using HttpRequestMessage request = new(HttpMethod.Options, "/");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using HttpResponseMessage response = await client.SendAsync(request, token);

        Assert.That(response.StatusCode, Is.EqualTo(expected));
        if (expected == HttpStatusCode.NoContent)
            Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { origin }));
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Shared_listener_retains_both_address_families_with_the_same_configured_port(CancellationToken token)
    {
        if (!Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable on this host.");
        await using TestHost runtime = await TestHost.CreateAsync("http://127.0.0.1:0;http://[::1]:0", token);
        await runtime.Host.StartAsync(token);
        Uri[] addresses = runtime.Addresses;
        Assert.That(addresses, Has.Length.EqualTo(2));
        Assert.That(addresses.Select(static uri => uri.Host), Is.EquivalentTo(new[] { "127.0.0.1", "[::1]" }));
        foreach (Uri address in addresses)
        {
            using HttpClient client = new() { BaseAddress = address };
            using StringContent request = new(ChainIdRequest, Encoding.UTF8, "application/json");
            using HttpResponseMessage message = await client.PostAsync("/", request, token);
            using JsonDocument response = JsonDocument.Parse(await message.Content.ReadAsStringAsync(token));
            Assert.That(response.RootElement.GetProperty("result").GetString(), Is.EqualTo("0x1"));
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Shared_listener_waits_for_sync_cleanup_before_provider_disposal(CancellationToken token)
    {
        TestHost runtime = await TestHost.CreateAsync("http://127.0.0.1:0", token);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(runtime.Host, async stop =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            cancelled.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await started.Task.WaitAsync(token);
            runtime.Lifetime.StopApplication();
            await cancelled.Task.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(runtime.Disposal.Count, Is.Zero);
            }
            release.TrySetResult();
            await run.WaitAsync(token);
            Assert.That(runtime.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        }
        finally
        {
            release.TrySetResult();
            runtime.Lifetime.StopApplication();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await runtime.DisposeAsync();
        }
        Assert.That(runtime.Disposal.Count, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Concurrent_and_reentrant_stops_wait_for_request_cleanup_before_provider_disposal(CancellationToken token)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposedDuringRequest = -1;
        await using TestHost runtime = await TestHost.CreateAsync("http://127.0.0.1:0", token, handler: async context =>
        {
            DisposalProbe dependency = context.RequestServices.GetRequiredService<DisposalProbe>();
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            disposedDuringRequest = Volatile.Read(ref dependency.Count);
            await context.Response.WriteAsync("drained", context.RequestAborted);
        });
        await runtime.Host.StartAsync(token);
        using HttpClient client = new() { BaseAddress = runtime.Address };
        Task<string> response = client.GetStringAsync("/", token);
        Task? reentrantStop = null;
        using CancellationTokenRegistration stopping = runtime.Lifetime.ApplicationStopping.Register(() =>
            reentrantStop = runtime.Host.StopAsync(token));
        try
        {
            await started.Task.WaitAsync(token);
            Task firstStop = runtime.Host.StopAsync(token);
            Task secondStop = runtime.Host.StopAsync(token);
            Task disposal = runtime.DisposeAsync().AsTask();
            Assert.That(reentrantStop, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstStop.IsCompleted, Is.False);
                Assert.That(secondStop.IsCompleted, Is.False);
                Assert.That(reentrantStop!.IsCompleted, Is.False);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(runtime.Disposal.Count, Is.Zero);
                Assert.That(runtime.Lifetime.ApplicationStopped.IsCancellationRequested, Is.False);
            }

            release.TrySetResult();
            await Task.WhenAll(firstStop, secondStop, reentrantStop!, disposal).WaitAsync(token);
            string body = await response.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(body, Is.EqualTo("drained"));
                Assert.That(disposedDuringRequest, Is.Zero);
                Assert.That(runtime.Disposal.Count, Is.EqualTo(1));
                Assert.That(runtime.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
            }
        }
        finally
        {
            release.TrySetResult();
            await ((Task)response).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        internal int Count;
        public DisposalProbe() { }
        public void Dispose() => Interlocked.Increment(ref Count);
    }

    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly (string Key, string? Value)[] _previous;

        internal ScopedEnvironment(params (string Key, string? Value)[] values)
        {
            _previous = values.Select(static value => (value.Key, Environment.GetEnvironmentVariable(value.Key))).ToArray();
            foreach ((string key, string? value) in values) Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach ((string key, string? value) in _previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class TestHost(SharedWebHost host, RpcService rpc, ApplicationLifetime lifetime,
        DisposalProbe disposal) : IAsyncDisposable
    {
        internal SharedWebHost Host => host;
        internal ApplicationLifetime Lifetime => lifetime;
        internal DisposalProbe Disposal => disposal;
        internal Uri[] Addresses => host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Select(static address => new Uri(address)).ToArray();
        internal Uri Address
        {
            get
            {
                Uri address = Addresses.Single();
                return address.Host == "0.0.0.0" ? new UriBuilder(address) { Host = "127.0.0.1" }.Uri : address;
            }
        }

        internal static async Task<TestHost> CreateAsync(string url, CancellationToken token, string[]? allowedHosts = null,
            RequestDelegate? handler = null)
        {
            WebApplicationBuilder builder = LightClientHost.CreateBuilder(["--environment", Environments.Production, "--urls", url]);
            builder.Logging.ClearProviders();
            builder.Services.AddCors();
            builder.Services.AddRouting();
            LightClientStatus status = new(1, [], _ => false, _ => false);
            LightClientMetrics metrics = new("mainnet", status.Read);
            RpcService rpc = new(status, NullLogManager.Instance);
            rpc.Config.AllowedHosts = allowedHosts ?? ["*"];
            rpc.Config.CorsOrigins = [];
            JsonRpcUrlCollection urls = new(NullLogManager.Instance, new JsonRpcConfig(), includeWebSockets: true);
            List<JsonRpcUrl> listeners = [];
            foreach (string value in url.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                BindingAddress binding = BindingAddress.Parse(value);
                JsonRpcUrl configured = new(binding.Scheme, binding.Host, binding.Port,
                    RpcTransport.Http | RpcTransport.Ws, false, rpc.Config.EnabledModules);
                urls[binding.Port] = configured;
                listeners.Add(configured);
            }
            string[] listenUrls = await JsonRpcListenUrls.ResolveAsync(listeners, Dns.GetHostAddressesAsync, token);
            builder.Services.AddSingleton<IConfigProvider>(new ConfigProvider(rpc.Config));
            builder.Services.AddSingleton<IJsonRpcUrlCollection>(urls);
            builder.Services.AddSingleton<ApplicationLifetime>();
            builder.Services.Replace(ServiceDescriptor.Singleton<IHostApplicationLifetime>(services => services.GetRequiredService<ApplicationLifetime>()));
            builder.Services.AddSingleton<DisposalProbe>();
            builder.WebHost.UseUrls(listenUrls);
            builder.WebHost.ConfigureKestrel(static options => options.Limits.MaxRequestBodySize = Nethermind.LightClient.Rpc.RpcEndpoint.MaxRequestBodySize);
            WebApplication application = builder.Build();
            IJsonRpcProcessor processor = rpc.CreateProcessor(new RpcServiceDiagnostics(rpc.Service, NullLogger.Instance, metrics));
            IStartup startup = handler is null
                ? new LightClientStartup(status, processor, metrics, url, NullLogger.Instance)
                : new RequestStartup(handler);
            SharedWebHost host = new(application.Services, application.Configuration, startup, NullLogManager.Instance);
            return new(host, rpc, application.Services.GetRequiredService<ApplicationLifetime>(),
                application.Services.GetRequiredService<DisposalProbe>());
        }

        public async ValueTask DisposeAsync()
        {
            try { await host.DisposeAsync(); }
            finally { rpc.Dispose(); }
        }
    }

    private sealed class RequestStartup(RequestDelegate handler) : IStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services) => throw new NotSupportedException();
        public void Configure(IApplicationBuilder app) => app.Run(handler);
    }
}
