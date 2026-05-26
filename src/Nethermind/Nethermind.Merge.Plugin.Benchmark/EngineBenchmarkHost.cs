// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Shared TestServer + JSON-RPC processor wiring for the engine API benchmarks.
/// Both benchmark classes use identical authentication, URL-collection, header,
/// withdrawal, and JSON-RPC server setup; only the SSZ side differs by which
/// handler list it registers.
/// </summary>
internal static class EngineBenchmarkHost
{
    public const int EnginePort = 8551;
    public const int CapellaMaxWithdrawals = 16;

    private const string BearerToken =
        "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" +
        ".eyJpYXQiOjE3MDAwMDAwMDB9" +
        ".stubTokenForBenchmarkingOnly";

    public static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");
    public static readonly MediaTypeHeaderValue ApplicationJson = new("application/json");
    public static readonly AuthenticationHeaderValue Authorization = AuthenticationHeaderValue.Parse(BearerToken);
    public static readonly MediaTypeWithQualityHeaderValue OctetAccept = new("application/octet-stream");

    public static readonly EthereumJsonSerializer Serializer = new();

    /// <summary>
    /// Builds a Kestrel TestServer with the auth/URL-collection plumbing that
    /// <see cref="Nethermind.Merge.Plugin.SszRest.SszMiddleware"/> and the JSON-RPC
    /// fast-lane both expect. Caller supplies any extra services and the terminal
    /// middleware (SSZ middleware or JSON-RPC dispatcher).
    /// </summary>
    public static IHost Build(Action<IServiceCollection> configureServices, Action<IApplicationBuilder> configureApp)
    {
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(static b => b.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    JsonRpcUrl url = new("http", "localhost", EnginePort, RpcEndpoint.Http, isAuthenticated: true, ["engine"]);
                    services.AddSingleton<IJsonRpcUrlCollection>(new StubUrlCollection(EnginePort, url));
                    services.AddSingleton<IRpcAuthentication>(new StubRpcAuthentication());
                    services.AddSingleton<ILogManager>(LimboLogs.Instance);
                    services.AddSingleton<IProcessExitSource>(new StubProcessExitSource());
                    configureServices(services);
                });
                web.Configure(app =>
                {
                    // TestServer has no real port; patch LocalPort so URL-collection
                    // lookup matches the registered authenticated engine URL.
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.LocalPort = EnginePort;
                        await next();
                    });
                    configureApp(app);
                });
            })
            .Build();

        host.Start();
        return host;
    }

    /// <summary>
    /// Builds a TestServer hosting the production JSON-RPC processor with the
    /// supplied engine module registered as a singleton. Used as the JSON baseline
    /// for SSZ-vs-JSON benchmark comparisons.
    /// </summary>
    public static IHost BuildJsonServer(IEngineRpcModule engine)
    {
        JsonRpcConfig config = new();
        IFileSystem fs = Substitute.For<IFileSystem>();

        RpcModuleProvider modules = new(fs, config, Serializer, LimboLogs.Instance);
        modules.Register(new SingletonModulePool<IEngineRpcModule>(engine, allowExclusive: true));

        JsonRpcService service = new(modules, LimboLogs.Instance, config);
        JsonRpcProcessor processor = new(service, config, fs, LimboLogs.Instance);

        return Build(
            _ => { },
            app => app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method != "POST" ||
                    !(ctx.Request.ContentType?.Contains("application/json") ?? false))
                {
                    await next();
                    return;
                }

                IJsonRpcUrlCollection urls = ctx.RequestServices.GetRequiredService<IJsonRpcUrlCollection>();
                if (!urls.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated)
                {
                    await next();
                    return;
                }

                IRpcAuthentication auth = ctx.RequestServices.GetRequiredService<IRpcAuthentication>();
                string? authHeader = ctx.Request.Headers.Authorization;
                if (authHeader is null || !await auth.Authenticate(authHeader))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }

                using JsonRpcContext rpcContext = JsonRpcContext.Http(url);
                BenchmarkJsonRpcResponseSink sink = new(ctx);
                await processor.ProcessAsync(
                    ctx.Request.BodyReader,
                    rpcContext,
                    sink,
                    new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument),
                    ctx.RequestAborted);
            }));
    }

    public static Withdrawal[] BuildWithdrawals(int count)
    {
        Withdrawal[] withdrawals = new Withdrawal[count];
        for (int i = 0; i < count; i++)
        {
            withdrawals[i] = new Withdrawal
            {
                Index = (ulong)i,
                ValidatorIndex = (ulong)(i + 1),
                Address = TestItem.Addresses[i % TestItem.Addresses.Length],
                AmountInGwei = (ulong)((i + 1) * 1000),
            };
        }
        return withdrawals;
    }

    private sealed class StubUrlCollection(int port, JsonRpcUrl url) : IJsonRpcUrlCollection
    {
        private readonly Dictionary<int, JsonRpcUrl> _inner = new() { [port] = url };

        public string[] Urls { get; } = [url.ToString()];
        public JsonRpcUrl this[int key] => _inner[key];
        public IEnumerable<int> Keys => _inner.Keys;
        public IEnumerable<JsonRpcUrl> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(int key) => _inner.ContainsKey(key);

        public bool TryGetValue(int key, out JsonRpcUrl value)
        {
            if (_inner.TryGetValue(key, out JsonRpcUrl? found))
            {
                value = found;
                return true;
            }
            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<int, JsonRpcUrl>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class StubRpcAuthentication : IRpcAuthentication
    {
        public Task<bool> Authenticate(string token) => Task.FromResult(true);
    }

    private sealed class StubProcessExitSource : IProcessExitSource
    {
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) { }
    }

    private sealed class BenchmarkJsonRpcResponseSink(HttpContext context) : IJsonRpcResponseSink
    {
        private bool _isFirstBatchItem = true;

        public long BytesWritten => 0;
        public bool StopRequested => false;

        public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            EnsureStarted();
            await Serializer.SerializeAsync(context.Response.BodyWriter, response);
            await context.Response.CompleteAsync();
        }

        public async ValueTask BeginBatchAsync(CancellationToken cancellationToken)
        {
            EnsureStarted();
            await JsonRpcResponseWriter.WriteBatchStartAsync(context.Response.Body, cancellationToken);
        }

        public async ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            if (!_isFirstBatchItem)
            {
                await JsonRpcResponseWriter.WriteBatchSeparatorAsync(context.Response.Body, cancellationToken);
            }

            _isFirstBatchItem = false;
            await Serializer.SerializeAsync(context.Response.BodyWriter, response);
        }

        public async ValueTask EndBatchAsync(CancellationToken cancellationToken)
        {
            await JsonRpcResponseWriter.WriteBatchEndAsync(context.Response.Body, cancellationToken);
            await context.Response.CompleteAsync();
        }

        private void EnsureStarted()
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
        }
    }
}
