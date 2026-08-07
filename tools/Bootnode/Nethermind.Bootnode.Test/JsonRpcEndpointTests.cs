// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class JsonRpcEndpointTests
{
    [TestCase("1")]
    [TestCase("\"not-object\"")]
    [TestCase("[1]")]
    public async Task Invalid_non_object_request_returns_json_rpc_error(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);
        JsonElement responseElement = response.RootElement.ValueKind == JsonValueKind.Array
            ? response.RootElement[0]
            : response.RootElement;

        Assert.That(responseElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
    }

    [Test]
    public async Task Successful_response_omits_error_property()
    {
        using JsonDocument document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"bootnode_status"}""");
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.TryGetProperty("result", out _), Is.True);
            Assert.That(response.RootElement.TryGetProperty("error", out _), Is.False);
        }
    }

    [Test]
    public async Task Error_response_omits_result_property()
    {
        using JsonDocument document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"unknown"}""");
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.TryGetProperty("error", out _), Is.True);
            Assert.That(response.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    private static BootnodeStatus CreateStatus() =>
        new(
            new BootnodeIdentity("enode://local", "enr:local", 1, "node", "address"),
            ["discv5"],
            ActiveDiscovery: true,
            DiscoveryPort: 30303,
            HttpPort: 8546,
            MetricsPort: 6060);

    private static async Task<JsonDocument> ExecuteJson(IResult result)
    {
        DefaultHttpContext context = new();
        await using MemoryStream body = new();
        context.Response.Body = body;
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        context.RequestServices = serviceProvider;

        await result.ExecuteAsync(context);

        body.Position = 0;
        return await JsonDocument.ParseAsync(body);
    }
}
