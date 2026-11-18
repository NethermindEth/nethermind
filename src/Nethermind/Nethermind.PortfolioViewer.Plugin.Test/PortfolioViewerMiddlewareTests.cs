// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.PortfolioViewer.Plugin.Test;

[TestFixture]
public class PortfolioViewerMiddlewareTests
{
    private const int Port = 8545;
    private const int SiblingPort = 8546;
    private const string SiblingChainId = "0x64";

    [TestCase(false, StatusCodes.Status200OK)]
    [TestCase(true, StatusCodes.Status404NotFound)]
    public async Task Serves_OnlyOnNonAuthenticatedPorts(bool isAuthenticated, int expectedStatusCode)
    {
        (DefaultHttpContext ctx, MemoryStream responseBody) = CreateContext(Port);

        await CreateMiddleware(isAuthenticated: isAuthenticated).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(expectedStatusCode));
        if (expectedStatusCode == StatusCodes.Status200OK)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Response.ContentType, Does.StartWith("text/html"));
                Assert.That(Encoding.UTF8.GetString(responseBody.ToArray()), Does.Contain("Nethermind Portfolio"));
            }
        }
    }

    [TestCase("/portfolio-sw.js", "text/javascript", "notificationclick")]
    [TestCase("/portfolio.webmanifest", "application/manifest+json", "Nethermind Portfolio")]
    [TestCase("/portfolio-icon.svg", "image/svg+xml", "<svg")]
    public async Task Serves_EmbeddedAssets(string path, string expectedContentType, string expectedContent)
    {
        (DefaultHttpContext ctx, MemoryStream responseBody) = CreateContext(Port, path: path);

        await CreateMiddleware().InvokeAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(ctx.Response.ContentType, Does.StartWith(expectedContentType));
            Assert.That(Encoding.UTF8.GetString(responseBody.ToArray()), Does.Contain(expectedContent));
        }
    }

    [Test]
    public async Task UnknownPort_Returns404()
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port + 1);

        await CreateMiddleware().InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [TestCase("POST", "/portfolio")]
    [TestCase("GET", "/other")]
    public async Task OtherRequests_PassThrough(string method, string path)
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, method, path);
        bool nextCalled = false;

        PortfolioViewerMiddleware middleware = new(
            _ => { nextCalled = true; return Task.CompletedTask; },
            CreateUrlCollection(),
            Substitute.For<ISiblingNodeRegistry>(),
            Substitute.For<IDetectionCache>(),
            Substitute.For<IDetectionScanner>(),
            Substitute.For<IPinnedCidStore>());
        await middleware.InvokeAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextCalled, Is.True);
            Assert.That(ctx.Response.HasStarted, Is.False);
        }
    }

    [TestCase(false, StatusCodes.Status200OK)]
    [TestCase(true, StatusCodes.Status404NotFound)]
    public async Task NodesList_OnlyOnNonAuthenticatedPorts(bool isAuthenticated, int expectedStatusCode)
    {
        (DefaultHttpContext ctx, MemoryStream responseBody) = CreateContext(Port, path: "/portfolio-nodes");
        ISiblingNodeRegistry siblings = Substitute.For<ISiblingNodeRegistry>();
        siblings.GetSiblingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SiblingNode>)[new SiblingNode(SiblingPort, SiblingChainId)]);

        await CreateMiddleware(isAuthenticated: isAuthenticated, siblings: siblings).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(expectedStatusCode));
        if (expectedStatusCode == StatusCodes.Status200OK)
        {
            string body = Encoding.UTF8.GetString(responseBody.ToArray());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Response.ContentType, Is.EqualTo("application/json"));
                Assert.That(body, Does.Contain(SiblingPort.ToString()));
                Assert.That(body, Does.Contain(SiblingChainId));
            }
        }
    }

    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId"}""", true)]
    [TestCase("""[{"jsonrpc":"2.0","id":1,"method":"eth_chainId"},{"jsonrpc":"2.0","id":2,"method":"eth_blockNumber"}]""", true)]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"example_getStatus"}""", false)]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","METHOD":"eth_blockNumber"}""", false)]
    public async Task Proxy_KnownSibling_ForwardsPortfolioMethods(string body, bool expectedForward)
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, method: "POST", path: $"/portfolio-rpc/{SiblingPort}", body: body);
        ISiblingNodeRegistry siblings = Substitute.For<ISiblingNodeRegistry>();
        siblings.IsKnownSibling(SiblingPort).Returns(true);

        await CreateMiddleware(siblings: siblings).InvokeAsync(ctx);

        await siblings.Received(expectedForward ? 1 : 0)
            .ProxyAsync(SiblingPort, Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(expectedForward ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task Proxy_UnknownSibling_Returns404()
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, method: "POST", path: "/portfolio-rpc/9999");
        ISiblingNodeRegistry siblings = Substitute.For<ISiblingNodeRegistry>();
        siblings.IsKnownSibling(9999).Returns(false);

        await CreateMiddleware(siblings: siblings).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        await siblings.DidNotReceive().ProxyAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [TestCase("portfolio-rpc", false)]
    [TestCase("portfolio-rpc", true)]
    [TestCase("portfolio-detect-rpc", false)]
    [TestCase("portfolio-detect-rpc", true)]
    public async Task Proxy_rejects_a_body_over_one_mebibyte(string route, bool knownLength)
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, method: "POST", path: $"/{route}/{SiblingPort}");
        using MemoryStream request = new(new byte[1024 * 1024 + 2]);
        ctx.Request.Body = request;
        ctx.Request.ContentLength = knownLength ? request.Length : null;
        ISiblingNodeRegistry siblings = Substitute.For<ISiblingNodeRegistry>();
        siblings.IsKnownSibling(SiblingPort).Returns(true);

        await CreateMiddleware(siblings: siblings).InvokeAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
            Assert.That(request.Position, Is.EqualTo(knownLength ? 0 : 1024 * 1024 + 1));
        }
        await siblings.DidNotReceive().ProxyAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await siblings.DidNotReceive().ProxyDetectAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Detect_proxy_forwards_only_the_detection_request()
    {
        const string address = "0x0000000000000000000000000000000000000001";
        string request = $$"""{"chainId":1,"address":"{{address}}","extra":true}""";
        (DefaultHttpContext ctx, _) = CreateContext(
            Port, method: "POST", path: $"/portfolio-detect-rpc/{SiblingPort}", body: request);
        ISiblingNodeRegistry siblings = Substitute.For<ISiblingNodeRegistry>();
        siblings.IsKnownSibling(SiblingPort).Returns(true);
        string? forwarded = null;
        siblings.ProxyDetectAsync(SiblingPort, Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using StreamReader reader = new(call.ArgAt<Stream>(1), leaveOpen: true);
                forwarded = reader.ReadToEnd();
                return Task.CompletedTask;
            });

        await CreateMiddleware(siblings: siblings).InvokeAsync(ctx);

        Assert.That(forwarded, Is.EqualTo($$"""{"chainId":1,"address":"{{address}}"}"""));
    }

    [Test]
    public async Task SideEffecting_CrossOriginRequest_Rejected()
    {
        (DefaultHttpContext ctx, _) = CreateContext(
            Port, method: "POST", path: "/portfolio-detect", origin: "http://evil.example", host: $"localhost:{Port}");
        IDetectionScanner scanner = Substitute.For<IDetectionScanner>();

        await CreateMiddleware(scanner: scanner).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        scanner.DidNotReceive().RequestScan(Arg.Any<long>(), Arg.Any<Address>());
    }

    [Test]
    public async Task SideEffecting_SameOriginRequest_Allowed()
    {
        (DefaultHttpContext ctx, _) = CreateContext(
            Port, method: "POST", path: "/portfolio-detect", origin: $"http://localhost:{Port}", host: $"localhost:{Port}",
            body: """{"chainId":1,"address":"0xd8da6bf26964af9d7eed9e03e53415d37aa96045"}""");
        IDetectionScanner scanner = Substitute.For<IDetectionScanner>();

        await CreateMiddleware(scanner: scanner).InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status403Forbidden));
        scanner.Received(1).RequestScan(1, Arg.Any<Address>());
    }

    [Test]
    public async Task Ipfs_InvalidRef_Returns400([Values("/portfolio-ipfs/../portfolio", "/portfolio-ipfs/foo%20bar", "/portfolio-ipfs")] string path)
    {
        (DefaultHttpContext ctx, _) = CreateContext(Port, path: path);

        await CreateMiddleware().InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    private static PortfolioViewerMiddleware CreateMiddleware(bool isAuthenticated = false, ISiblingNodeRegistry? siblings = null, IDetectionScanner? scanner = null, IPinnedCidStore? pins = null) =>
        new(_ => Task.CompletedTask, CreateUrlCollection(isAuthenticated), siblings ?? Substitute.For<ISiblingNodeRegistry>(), Substitute.For<IDetectionCache>(), scanner ?? Substitute.For<IDetectionScanner>(), pins ?? Substitute.For<IPinnedCidStore>());

    private static IJsonRpcUrlCollection CreateUrlCollection(bool isAuthenticated = false) =>
        new TestJsonRpcUrlCollection(new JsonRpcUrl("http", "127.0.0.1", Port, RpcEndpoint.Http, isAuthenticated, [ModuleType.Eth]));

    private static (DefaultHttpContext Context, MemoryStream ResponseBody) CreateContext(
        int localPort, string method = "GET", string path = "/portfolio", string? origin = null, string? host = null, string? body = null)
    {
        DefaultHttpContext ctx = new()
        {
            Request = { Method = method, Path = path }
        };
        ctx.Connection.LocalPort = localPort;
        if (host is not null) ctx.Request.Host = new HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        if (body is not null) ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
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
