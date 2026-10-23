// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.JsonRpc;
using Nethermind.Runner.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class WebSocketOriginFilterTests
{
    private const string AllowedOrigin = "https://app.example";
    private const string OtherOrigin = "https://evil.example";

    [TestCase(new[] { AllowedOrigin }, AllowedOrigin, ExpectedResult = true, TestName = "Accepts an allowed origin")]
    [TestCase(new[] { AllowedOrigin }, OtherOrigin, ExpectedResult = false, TestName = "Rejects another origin")]
    [TestCase(new[] { AllowedOrigin }, "", ExpectedResult = true, TestName = "Accepts a request without an origin")]
    [TestCase(new[] { "https://App.Example" }, AllowedOrigin, ExpectedResult = true, TestName = "Ignores the case of a configured origin")]
    [TestCase(new[] { "*" }, OtherOrigin, ExpectedResult = true, TestName = "Accepts any origin with the wildcard")]
    [TestCase(new string[0], OtherOrigin, ExpectedResult = false, TestName = "Rejects every origin with no CORS origins")]
    public bool IsAllowed_follows_the_cors_policy(string[] corsOrigins, string origin) =>
        WebSocketOriginFilter.IsAllowed(origin, Startup.CreateCorsPolicy(new JsonRpcConfig { CorsOrigins = corsOrigins }));

    [Test]
    public async Task Handshake_is_refused_for_another_origin_before_the_socket_is_accepted()
    {
        await using WebApplication app = await StartAsync(new JsonRpcConfig { CorsOrigins = [AllowedOrigin] });
        Uri uri = WebSocketUri(app);

        using (ClientWebSocket allowed = await ConnectAsync(uri, AllowedOrigin))
        {
            Assert.That(allowed.State, Is.EqualTo(WebSocketState.Open));
        }

        using (ClientWebSocket nonBrowser = await ConnectAsync(uri, origin: null))
        {
            Assert.That(nonBrowser.State, Is.EqualTo(WebSocketState.Open));
        }

        WebSocketException? refused = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(uri, OtherOrigin));
        Assert.That(refused!.Message, Does.Contain("403"));
    }

    /// <summary>Serves WebSockets the way <see cref="Startup"/> does: the origin filter runs ahead of the socket handler.</summary>
    private static async Task<WebApplication> StartAsync(IJsonRpcConfig config)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddLogging();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, 0));

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions());
        app.UseWhen(static ctx => ctx.WebSockets.IsWebSocketRequest, branch =>
        {
            branch.UseWebSocketOriginFilter(Startup.CreateCorsPolicy(config));
            branch.Run(static async ctx =>
            {
                using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            });
        });

        await app.StartAsync();
        return app;
    }

    private static Uri WebSocketUri(WebApplication app)
    {
        Uri http = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return new UriBuilder(http) { Scheme = "ws" }.Uri;
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri uri, string? origin)
    {
        ClientWebSocket socket = new();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
