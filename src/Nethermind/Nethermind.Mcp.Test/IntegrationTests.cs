// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Mcp.Hosting;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test;

/// <summary>
/// End-to-end integration tests that drive the plugin's <see cref="McpWebHost"/> via the
/// SDK's in-process MCP client over Streamable HTTP. Validates that tools, resources, and
/// the auth gate are wired correctly from the SDK's perspective — not just that each
/// component works in isolation.
/// </summary>
[TestFixture]
public class IntegrationTests
{
    /// <summary>
    /// Builds a <see cref="McpWebHost"/> bound to an ephemeral port with the supplied API
    /// substitute and (optionally) an API key. Caller owns the host and must dispose it via
    /// <c>await using</c>; the underlying Autofac container is rooted by the host's lifetime
    /// (it holds the singleton tool/resource references for the request pipeline) and is
    /// reclaimed by GC once the host is disposed.
    /// </summary>
    private static McpWebHost CreateHost(
        INethermindApi api,
        IConfigProvider configProvider,
        string? apiKey = null)
    {
        ContainerBuilder builder = new();
        builder.RegisterInstance(api).As<INethermindApi>();
        builder.RegisterInstance(configProvider).As<IConfigProvider>();
        builder.RegisterModule(new McpServerPluginModule());
        IContainer container = builder.Build();
        AutofacResolverServiceProvider services = new(container);

        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
            ApiKey = apiKey,
            MaxConcurrent = 4,
        };

        return new McpWebHost(config, LimboLogs.Instance, services);
    }

    private static INethermindApi BuildApiSubstitute(Block? head = null)
    {
        INethermindApi api = Substitute.For<INethermindApi>();

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block headBlock = head ?? Build.A.Block.WithNumber(100).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.BestSuggestedHeader.Returns(headBlock.Header);
        api.BlockTree.Returns(blockTree);

        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.PeerCount.Returns(5);
        api.SyncPeerPool.Returns(peerPool);

        return api;
    }

    private static async Task<McpClient> ConnectAsync(McpWebHost host, CancellationToken ct = default)
    {
        Uri endpoint = new(host.BoundUri!, "/mcp");
        HttpClientTransport transport = new(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "Nethermind.Mcp.Test",
        }, loggerFactory: null!);

        return await McpClient.CreateAsync(transport, clientOptions: null!, loggerFactory: null!, cancellationToken: ct);
    }

    [Test]
    public async Task Lists_all_v1_tools()
    {
        INethermindApi api = BuildApiSubstitute();
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();

        await using McpWebHost host = CreateHost(api, configProvider);
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        await using McpClient client = await ConnectAsync(host);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        HashSet<string> names = tools.Select(static t => t.Name).ToHashSet();
        Assert.That(names, Does.Contain("get_sync_status"));
        Assert.That(names, Does.Contain("get_node_health"));
        Assert.That(names, Does.Contain("get_node_version"));
        Assert.That(names, Does.Contain("get_block"));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Lists_v1_resources()
    {
        INethermindApi api = BuildApiSubstitute();
        ConfigProvider configProvider = new();
        configProvider.Initialize();

        await using McpWebHost host = CreateHost(api, configProvider);
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        await using McpClient client = await ConnectAsync(host);
        IList<McpClientResource> resources = await client.ListResourcesAsync();

        HashSet<string> uris = resources.Select(static r => r.Uri).ToHashSet();
        Assert.That(uris, Does.Contain("nethermind://node/status"));
        Assert.That(uris, Does.Contain("nethermind://node/config"));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Calls_get_sync_status_returns_expected_block_numbers()
    {
        INethermindApi api = BuildApiSubstitute();
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();

        await using McpWebHost host = CreateHost(api, configProvider);
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        await using McpClient client = await ConnectAsync(host);
        CallToolResult result = await client.CallToolAsync("get_sync_status");

        Assert.That(result.IsError, Is.Not.True);
        JsonElement payload = ExtractToolPayload(result);
        Assert.That(payload.GetProperty("currentBlock").GetInt64(), Is.EqualTo(100));
        Assert.That(payload.GetProperty("peerCount").GetInt32(), Is.EqualTo(5));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Calls_get_block_with_latest_returns_block_summary()
    {
        Block head = Build.A.Block.WithNumber(100).TestObject;
        INethermindApi api = BuildApiSubstitute(head);
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();

        await using McpWebHost host = CreateHost(api, configProvider);
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        await using McpClient client = await ConnectAsync(host);
        CallToolResult result = await client.CallToolAsync(
            "get_block",
            new Dictionary<string, object?> { ["blockId"] = "latest" });

        Assert.That(result.IsError, Is.Not.True);
        JsonElement payload = ExtractToolPayload(result);
        Assert.That(payload.GetProperty("number").GetInt64(), Is.EqualTo(100));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Reads_node_status_resource()
    {
        INethermindApi api = BuildApiSubstitute();
        ConfigProvider configProvider = new();
        configProvider.Initialize();

        await using McpWebHost host = CreateHost(api, configProvider);
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        await using McpClient client = await ConnectAsync(host);
        ReadResourceResult result = await client.ReadResourceAsync("nethermind://node/status");

        Assert.That(result.Contents, Is.Not.Empty);
        TextResourceContents text = (TextResourceContents)result.Contents[0];
        using JsonDocument doc = JsonDocument.Parse(text.Text);
        JsonElement sync = doc.RootElement.GetProperty("sync");
        Assert.That(sync.GetProperty("currentBlock").GetInt64(), Is.EqualTo(100));
        Assert.That(sync.GetProperty("peerCount").GetInt32(), Is.EqualTo(5));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Bad_apiKey_returns_401()
    {
        INethermindApi api = BuildApiSubstitute();
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();

        await using McpWebHost host = CreateHost(api, configProvider, apiKey: "secret");
        Assert.That(await host.StartAsync(CancellationToken.None), Is.True);

        using HttpClient httpClient = new() { BaseAddress = host.BoundUri };
        using HttpResponseMessage response = await httpClient.GetAsync("/mcp");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Extracts the JSON payload from a tool call. The SDK 1.1 surface puts the result
    /// either in <see cref="CallToolResult.StructuredContent"/> (when the tool declares a
    /// JSON output schema) or, falling back, in the first text <see cref="ContentBlock"/>.
    /// We accept either to keep the test robust to SDK schema-inference behaviour.
    /// </summary>
    private static JsonElement ExtractToolPayload(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return UnwrapResultEnvelope(structured);
        }

        Assert.That(result.Content, Is.Not.Empty);
        TextContentBlock text = (TextContentBlock)result.Content[0];
        using JsonDocument doc = JsonDocument.Parse(text.Text);
        return UnwrapResultEnvelope(doc.RootElement.Clone());
    }

    /// <summary>
    /// Some SDK versions wrap a single-value tool result under a "result" property
    /// (the tools-output-schema envelope). Unwrap when present so callers can access
    /// tool DTO properties directly.
    /// </summary>
    private static JsonElement UnwrapResultEnvelope(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("result", out JsonElement wrapped)
            ? wrapped
            : element;

    /// <summary>
    /// Bridges an Autofac container to <see cref="IServiceProvider"/> so the SDK can resolve
    /// tool and resource singletons constructed by <see cref="McpServerPluginModule"/>.
    /// </summary>
    private sealed class AutofacResolverServiceProvider(IContainer container) : IServiceProvider, IDisposable
    {
        public object? GetService(Type serviceType) => container.ResolveOptional(serviceType);

        public void Dispose() => container.Dispose();
    }
}
