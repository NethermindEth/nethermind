// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Crypto;
using Nethermind.Stats.Model;
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

    [TestCase("""{"id":1,"method":"bootnode_status"}""")]
    [TestCase("""{"jsonrpc":"1.0","id":1,"method":"bootnode_status"}""")]
    [TestCase("""{"jsonrpc":2.0,"id":1,"method":"bootnode_status"}""")]
    public async Task Invalid_json_rpc_version_returns_invalid_request(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
    }

    [TestCase("true")]
    [TestCase("{}")]
    [TestCase("[]")]
    public async Task Invalid_id_returns_invalid_request(string id)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"jsonrpc":"2.0","id":{{id}},"method":"bootnode_status"}""");
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
        }
    }

    [TestCase("1.5")]
    [TestCase("1234567890123456789012345678901234567890")]
    public async Task Numeric_id_is_echoed_without_range_or_integer_restrictions(string id)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"jsonrpc":"2.0","id":{{id}},"method":"bootnode_status"}""");
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        Assert.That(response.RootElement.GetProperty("id").GetRawText(), Is.EqualTo(id));
    }

    [Test]
    public async Task Notification_returns_no_response()
    {
        using JsonDocument document = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"bootnode_status"}""");
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        (int statusCode, byte[] body) = await Execute(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCodes.Status204NoContent));
            Assert.That(body, Is.Empty);
        }
    }

    [Test]
    public async Task Batch_filters_notifications_from_responses()
    {
        using JsonDocument document = JsonDocument.Parse("""
            [
              {"jsonrpc":"2.0","method":"bootnode_status"},
              {"jsonrpc":"2.0","id":1,"method":"bootnode_status"}
            ]
            """);
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        using JsonDocument response = await ExecuteJson(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(response.RootElement[0].GetProperty("id").GetInt64(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Notification_only_batch_returns_no_response()
    {
        using JsonDocument document = JsonDocument.Parse("""
            [
              {"jsonrpc":"2.0","method":"bootnode_status"},
              {"jsonrpc":"2.0","method":"bootnode_nodeInfo"}
            ]
            """);
        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());

        (int statusCode, byte[] body) = await Execute(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCodes.Status204NoContent));
            Assert.That(body, Is.Empty);
        }
    }

    [Test]
    public async Task Node_method_applies_pagination_params()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey firstKey = generator.Generate();
        using PrivateKey secondKey = generator.Generate();
        DiscoveredNodeStore store = new();
        store.AddOrUpdate(new Node(firstKey.PublicKey, "127.0.0.1", 30303), "discv4", isActive: true);
        store.AddOrUpdate(new Node(secondKey.PublicKey, "127.0.0.1", 30304), "discv4", isActive: true);
        string expectedNodeId = store.GetAllNodes(offset: 1, limit: 1)[0].NodeId;
        using JsonDocument document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"bootnode_allNodes","params":{"offset":1,"limit":1}}""");

        IResult result = JsonRpcEndpoint.Handle(document.RootElement, store, CreateStatus());
        using JsonDocument response = await ExecuteJson(result);

        JsonElement nodes = response.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.GetArrayLength(), Is.EqualTo(1));
            Assert.That(nodes[0].GetProperty("nodeId").GetString(), Is.EqualTo(expectedNodeId));
        }
    }

    [TestCase("{\"offset\":-1}")]
    [TestCase("{\"limit\":1001}")]
    [TestCase("{\"offest\":1}")]
    [TestCase("{\"Offset\":1}")]
    [TestCase("{\"offset\":1,\"offset\":2}")]
    [TestCase("null")]
    [TestCase("\"invalid\"")]
    public async Task Invalid_node_pagination_params_return_json_rpc_error(string parameters)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"jsonrpc":"2.0","id":1,"method":"bootnode_allNodes","params":{{parameters}}}""");

        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());
        using JsonDocument response = await ExecuteJson(result);

        Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
    }

    [TestCase(0, false)]
    [TestCase(JsonRpcEndpoint.MaxBatchSize, true)]
    [TestCase(JsonRpcEndpoint.MaxBatchSize + 1, false)]
    public async Task Batch_size_is_bounded(int batchSize, bool valid)
    {
        string payload = JsonSerializer.Serialize(
            Enumerable.Range(0, batchSize)
                .Select(static id => new { jsonrpc = "2.0", id, method = "bootnode_status" }));
        using JsonDocument document = JsonDocument.Parse(payload);

        IResult result = JsonRpcEndpoint.Handle(document.RootElement, new DiscoveredNodeStore(), CreateStatus());
        using JsonDocument response = await ExecuteJson(result);

        if (valid)
        {
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(batchSize));
        }
        else
        {
            Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
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
        (int _, byte[] body) = await Execute(result);
        return JsonDocument.Parse(body);
    }

    private static async Task<(int StatusCode, byte[] Body)> Execute(IResult result)
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
        return (context.Response.StatusCode, body.ToArray());
    }
}
