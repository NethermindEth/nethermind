// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json;
using Autofac;
using Autofac.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Client;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpSecurityTests
{
    private const int Port = 8555;
    private const string AllowedOrigin = "http://localhost:6274";
    private const string Token = McpTestNode.TestToken;

    [TestCase("127.0.0.1:8555")]
    [TestCase("localhost:8555")]
    [TestCase("LOCALHOST:8555")]
    [TestCase("[::1]:8555")]
    public void Loopback_host_header_is_allowed(string host) =>
        Assert.That(McpSecurityMiddleware.IsAllowedHost(host, Port), Is.True);

    [TestCase("evil.com")]
    [TestCase("evil.com:8555")]
    [TestCase("localhost.evil.com:8555")]
    [TestCase("127.0.0.1.nip.io:8555")]
    [TestCase("127.0.0.1:8545")] // right name, another listener's port
    [TestCase("127.0.0.1")] // port omitted on a non-default port
    [TestCase("127.0.0.1:")]
    [TestCase("127.0.0.1:+8555")]
    [TestCase("0.0.0.0:8555")]
    [TestCase("192.168.1.10:8555")]
    [TestCase("[::1]")]
    [TestCase("")]
    [TestCase(null)]
    public void Foreign_or_rebound_host_header_is_rejected(string? host) =>
        Assert.That(McpSecurityMiddleware.IsAllowedHost(host, Port), Is.False);

    [TestCase("node.example.com:8555", ExpectedResult = true)]
    [TestCase("node.example.com", ExpectedResult = true)] // an entry without a port matches any port
    [TestCase("NODE.Example.com:9999", ExpectedResult = true)]
    [TestCase("pinned.example.com:9000", ExpectedResult = true)]
    [TestCase("pinned.example.com:8555", ExpectedResult = false)] // an entry with a port matches only that port
    [TestCase("pinned.example.com", ExpectedResult = false)] // no port means the scheme default (80)
    [TestCase("other.example.com:8555", ExpectedResult = false)]
    [TestCase("node.example.com.evil:8555", ExpectedResult = false)]
    [TestCase("127.0.0.1:8555", ExpectedResult = true)] // loopback names stay accepted on loopback
    public bool Loopback_policy_accepts_allowed_hosts(string host) =>
        McpHostPolicy.Loopback(McpHostPolicy.DefaultHttpPort, McpConfigValidator.ParseAllowedHosts(["node.example.com", "pinned.example.com:9000"])).IsAllowed(host, Port);

    [TestCase("127.0.0.1", 443, ExpectedResult = true)]
    [TestCase("localhost", 443, ExpectedResult = true)]
    [TestCase("127.0.0.1", 80, ExpectedResult = false)] // over HTTPS, a portless host means 443
    public bool Https_loopback_policy_uses_443_as_default_port(string host, int localPort) =>
        McpHostPolicy.Loopback(McpHostPolicy.DefaultHttpsPort, []).IsAllowed(host, localPort);

    [TestCase("203.0.113.7", "203.0.113.7:8555", ExpectedResult = true)] // the bound IP literal
    [TestCase("203.0.113.7", "node.example.com:8555", ExpectedResult = true)]
    [TestCase("203.0.113.7", "node.example.com", ExpectedResult = true)]
    [TestCase("203.0.113.7", "127.0.0.1:8555", ExpectedResult = false)]
    [TestCase("203.0.113.7", "localhost:8555", ExpectedResult = false)]
    [TestCase("203.0.113.7", "[::1]:8555", ExpectedResult = false)]
    [TestCase("203.0.113.7", "203.0.113.8:8555", ExpectedResult = false)]
    [TestCase("0.0.0.0", "0.0.0.0:8555", ExpectedResult = false)] // a wildcard is not a name clients use
    [TestCase("::", "[::]:8555", ExpectedResult = false)]
    [TestCase("2001:db8::1", "[2001:DB8:0::1]:8555", ExpectedResult = true)] // IPv6 literals compare canonically
    [TestCase("0.0.0.0", "evil.com:8555", ExpectedResult = false)]
    [TestCase("0.0.0.0", "", ExpectedResult = false)]
    public bool Remote_policy_accepts_only_allowed_hosts_and_the_bound_literal(string bound, string host) =>
        McpHostPolicy.Remote(IPAddress.Parse(bound), McpConfigValidator.ParseAllowedHosts(["node.example.com"])).IsAllowed(host, Port);

    [TestCase(null, ExpectedResult = nameof(McpRequestVerdict.Allowed))]
    [TestCase(AllowedOrigin, ExpectedResult = nameof(McpRequestVerdict.Allowed))]
    [TestCase("HTTP://LOCALHOST:6274", ExpectedResult = nameof(McpRequestVerdict.Allowed))]
    [TestCase("http://localhost:6275", ExpectedResult = nameof(McpRequestVerdict.BadOrigin))]
    [TestCase("https://localhost:6274", ExpectedResult = nameof(McpRequestVerdict.BadOrigin))]
    [TestCase("https://evil.com", ExpectedResult = nameof(McpRequestVerdict.BadOrigin))]
    [TestCase("null", ExpectedResult = nameof(McpRequestVerdict.BadOrigin))]
    [TestCase("", ExpectedResult = nameof(McpRequestVerdict.BadOrigin))]
    public string Origin_is_checked_against_allow_list(string? origin) =>
        new McpSecurityMiddleware([AllowedOrigin], null).Evaluate("127.0.0.1:8555", Port, Values(origin), StringValues.Empty).ToString();

    [Test]
    public void Multiple_origin_headers_are_rejected() =>
        Assert.That(
            new McpSecurityMiddleware([AllowedOrigin], null).Evaluate("127.0.0.1:8555", Port, new StringValues([AllowedOrigin, AllowedOrigin]), StringValues.Empty),
            Is.EqualTo(McpRequestVerdict.BadOrigin));

    [TestCase(null, ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("", ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("Bearer", ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("Bearer ", ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("Basic " + Token, ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase(Token, ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("Bearer " + Token + "x", ExpectedResult = nameof(McpRequestVerdict.Unauthorized))]
    [TestCase("Bearer 0123456789abcdef0123456789abcdef", ExpectedResult = nameof(McpRequestVerdict.Unauthorized))] // a prefix of the token
    [TestCase("Bearer " + Token, ExpectedResult = nameof(McpRequestVerdict.Allowed))]
    [TestCase("bearer " + Token, ExpectedResult = nameof(McpRequestVerdict.Allowed))]
    public string Bearer_token_is_required_when_configured(string? authorization) =>
        new McpSecurityMiddleware([], Token).Evaluate("127.0.0.1:8555", Port, StringValues.Empty, Values(authorization)).ToString();

    [Test]
    public void Guards_apply_in_order_host_then_origin_then_auth()
    {
        McpSecurityMiddleware security = new([AllowedOrigin], Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(security.Evaluate("evil.com", Port, "https://evil.com", StringValues.Empty), Is.EqualTo(McpRequestVerdict.BadHost));
            Assert.That(security.Evaluate("127.0.0.1:8555", Port, "https://evil.com", StringValues.Empty), Is.EqualTo(McpRequestVerdict.BadOrigin));
            Assert.That(security.Evaluate("127.0.0.1:8555", Port, AllowedOrigin, StringValues.Empty), Is.EqualTo(McpRequestVerdict.Unauthorized));
            Assert.That(security.Evaluate("127.0.0.1:8555", Port, AllowedOrigin, $"Bearer {Token}"), Is.EqualTo(McpRequestVerdict.Allowed));
        }
    }

    private static IEnumerable<TestCaseData> HttpGuardCases()
    {
        yield return new TestCaseData(null, null, HttpStatusCode.OK).SetName("No_Origin_is_allowed");
        yield return new TestCaseData(AllowedOrigin, null, HttpStatusCode.OK).SetName("Allowed_Origin_is_allowed");
        yield return new TestCaseData("https://evil.com", null, HttpStatusCode.Forbidden).SetName("Foreign_Origin_is_forbidden");
        yield return new TestCaseData("null", null, HttpStatusCode.Forbidden).SetName("Opaque_Origin_is_forbidden");
        yield return new TestCaseData(null, "evil.com", HttpStatusCode.BadRequest).SetName("Foreign_Host_is_rejected");
        yield return new TestCaseData(null, "attacker.example:{port}", HttpStatusCode.BadRequest).SetName("Rebound_Host_is_rejected");
        yield return new TestCaseData(AllowedOrigin, "localhost:{port}", HttpStatusCode.OK).SetName("Localhost_Host_is_allowed");
    }

    [TestCaseSource(nameof(HttpGuardCases))]
    public async Task Http_guards_answer_before_mcp(string? origin, string? host, HttpStatusCode expected)
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.AllowedOrigins = [AllowedOrigin]);
        using HttpClient http = new();

        using HttpRequestMessage request = McpHttp.Post(node.Endpoint, McpHttp.InitializeBody(), origin, host?.Replace("{port}", node.Endpoint.Port.ToString()));
        using HttpResponseMessage response = await http.SendAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(expected));
            McpHttp.AssertNoCors(response);
        }
    }

    [Test]
    public async Task Initialize_on_the_listener_is_served_by_mcp()
    {
        await using McpTestNode node = await McpTestNode.Create();
        using HttpClient http = new();

        using HttpRequestMessage request = McpHttp.Post(node.Endpoint, McpHttp.InitializeBody());
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        JsonElement message = await McpHttp.ReadJsonRpc(response);
        JsonElement result = message.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.GetProperty("id").GetInt32(), Is.EqualTo(1));
            Assert.That(result.GetProperty("serverInfo").GetProperty("name").GetString(), Is.EqualTo("Nethermind"));
            Assert.That(result.GetProperty("protocolVersion").GetString(), Is.Not.Null.And.Not.Empty);
            Assert.That(result.GetProperty("capabilities").TryGetProperty("tools", out _), Is.True);
            McpHttp.AssertNoCors(response);
        }
    }

    [Test]
    public async Task Tools_list_over_raw_http_returns_every_tool()
    {
        await using McpTestNode node = await McpTestNode.Create();
        using HttpClient http = new();

        using HttpRequestMessage request = McpHttp.Post(node.Endpoint, McpHttp.ToolsListBody);
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        JsonElement tools = (await McpHttp.ReadJsonRpc(response)).GetProperty("result").GetProperty("tools");
        Assert.That(tools.EnumerateArray().Select(static t => t.GetProperty("name").GetString()), Is.EquivalentTo(McpAssert.ToolNames));
    }

    private static IEnumerable<TestCaseData> AuthCases()
    {
        yield return new TestCaseData(null, HttpStatusCode.Unauthorized).SetName("Missing_token_is_unauthorized");
        yield return new TestCaseData("wrong-token-wrong-token-wrong-token", HttpStatusCode.Unauthorized).SetName("Wrong_token_is_unauthorized");
        yield return new TestCaseData(Token, HttpStatusCode.OK).SetName("Right_token_is_allowed");
    }

    [TestCaseSource(nameof(AuthCases))]
    public async Task Bearer_auth_over_http(string? bearer, HttpStatusCode expected)
    {
        await using McpTestNode node = await McpTestNode.Create(withAuth: true);
        using HttpClient http = new();

        using HttpRequestMessage request = McpHttp.Post(node.Endpoint, McpHttp.InitializeBody(), bearer: bearer);
        using HttpResponseMessage response = await http.SendAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(expected));
            if (expected == HttpStatusCode.Unauthorized)
            {
                Assert.That(response.Headers.WwwAuthenticate.Select(static h => h.Scheme), Is.EqualTo(new[] { "Bearer" }));
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain(Token));
            }

            McpHttp.AssertNoCors(response);
        }
    }

    [Test]
    public async Task Sdk_client_authenticates_with_bearer_header()
    {
        await using McpTestNode node = await McpTestNode.Create(withAuth: true);

        await using McpClient client = await node.CreateClient(Token);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.That(tools.Select(static t => t.Name), Is.EquivalentTo(McpAssert.ToolNames));
    }

    [Test]
    public async Task Sdk_client_without_token_is_refused()
    {
        await using McpTestNode node = await McpTestNode.Create(withAuth: true);

        Assert.CatchAsync(async () =>
        {
            await using McpClient client = await node.CreateClient();
            await client.ListToolsAsync();
        });
    }

    [Test]
    public async Task Oversized_body_is_rejected_with_413()
    {
        const int limit = 1024;
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxRequestBodySize = limit);
        using HttpClient http = new();

        using HttpRequestMessage request = McpHttp.Post(node.Endpoint, McpHttp.InitializeBody(new string('x', 4 * limit)));
        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [TestCase("POST", "/")]
    [TestCase("POST", "/rpc")]
    [TestCase("POST", "/mcp/extra")]
    [TestCase("GET", "/")]
    [TestCase("GET", "/health")]
    public async Task Paths_other_than_mcp_are_not_found(string method, string path)
    {
        await using McpTestNode node = await McpTestNode.Create();
        using HttpClient http = new();

        using HttpRequestMessage request = method == "POST"
            ? McpHttp.Post(new Uri(node.Endpoint, path), McpHttp.InitializeBody())
            : new HttpRequestMessage(HttpMethod.Get, new Uri(node.Endpoint, path));
        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Cors_preflight_gets_no_cors_headers()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.AllowedOrigins = [AllowedOrigin]);
        using HttpClient http = new();

        using HttpRequestMessage request = new(HttpMethod.Options, node.Endpoint);
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type, authorization");
        using HttpResponseMessage response = await http.SendAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsSuccessStatusCode, Is.False, "preflight must not succeed");
            McpHttp.AssertNoCors(response);
            Assert.That(response.Headers.Contains("Access-Control-Allow-Headers"), Is.False);
            Assert.That(response.Headers.Contains("Access-Control-Allow-Methods"), Is.False);
        }
    }

    [Test]
    public async Task Listener_binds_loopback_only([Values("127.0.0.1", "::1")] string host)
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.Host = host);

        Uri endpoint = node.Endpoint;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IPAddress.IsLoopback(IPAddress.Parse(endpoint.Host.Trim('[', ']'))), Is.True, endpoint.ToString());
            Assert.That(endpoint.AbsolutePath, Is.EqualTo("/mcp"));
            Assert.That(endpoint.Port, Is.GreaterThan(0), "port 0 must resolve to the bound ephemeral port");
        }

        await using McpClient client = await node.CreateClient();
        Assert.That(await client.ListToolsAsync(), Has.Count.EqualTo(McpAssert.ToolNames.Length));
    }

    [Test]
    public void Module_registers_nothing_in_the_json_rpc_host()
    {
        using IContainer container = new ContainerBuilder().AddModule(new McpModule()).Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.ComponentRegistry.IsRegistered(new TypedService(typeof(IJsonRpcServiceConfigurer))), Is.False,
                "MCP must not be mounted on the JSON-RPC (or Engine API) host");
            Assert.That(container.ComponentRegistry.IsRegistered(new TypedService(typeof(IStartupFilter))), Is.False,
                "MCP must not add middleware to the JSON-RPC host");
        }
    }

    private static StringValues Values(string? value) => value is null ? StringValues.Empty : new StringValues(value);
}
