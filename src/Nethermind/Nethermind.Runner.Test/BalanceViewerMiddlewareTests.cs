// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nethermind.BalanceViewer.Plugin;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture]
public class BalanceViewerMiddlewareTests
{
    private const int Port = 8545;

    [TestCase(false, StatusCodes.Status200OK)]
    [TestCase(true, StatusCodes.Status404NotFound)]
    public async Task Serves_OnlyOnNonAuthenticatedPorts(bool isAuthenticated, int expectedStatusCode)
    {
        (DefaultHttpContext ctx, MemoryStream responseBody) = CreateContext(Port);

        await CreateMiddleware(isAuthenticated: isAuthenticated).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(expectedStatusCode));
        if (expectedStatusCode == StatusCodes.Status200OK)
        {
            Assert.That(ctx.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(Encoding.UTF8.GetString(responseBody.ToArray()), Does.Contain("Nethermind Balances"));
        }
    }

    [Test]
    public async Task UnknownPort_Returns404()
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port + 1);

        await CreateMiddleware().InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [TestCase("POST", "/balances")]
    [TestCase("GET", "/other")]
    public async Task OtherRequests_PassThrough(string method, string path)
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, method, path);
        bool nextCalled = false;

        BalanceViewerMiddleware middleware = new(_ => { nextCalled = true; return Task.CompletedTask; }, CreateUrlCollection());
        await middleware.InvokeAsync(ctx);

        Assert.That(nextCalled, Is.True);
        Assert.That(ctx.Response.HasStarted, Is.False);
    }

    private static BalanceViewerMiddleware CreateMiddleware(bool isAuthenticated = false) =>
        new(_ => Task.CompletedTask, CreateUrlCollection(isAuthenticated));

    private static IJsonRpcUrlCollection CreateUrlCollection(bool isAuthenticated = false) =>
        new TestJsonRpcUrlCollection(new JsonRpcUrl("http", "127.0.0.1", Port, RpcEndpoint.Http, isAuthenticated, [ModuleType.Eth]));

    private static (DefaultHttpContext Context, MemoryStream ResponseBody) CreateContext(int localPort, string method = "GET", string path = "/balances")
    {
        DefaultHttpContext ctx = new()
        {
            Request = { Method = method, Path = path }
        };
        ctx.Connection.LocalPort = localPort;
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        return (ctx, responseBody);
    }

    private sealed class TestJsonRpcUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
    {
        public TestJsonRpcUrlCollection(JsonRpcUrl url)
            : base(capacity: 1)
        {
            Add(url.Port, url);
            Urls = [url.ToString()];
        }

        public string[] Urls { get; }
    }
}
