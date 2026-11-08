// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

/// <summary>Routes calls to sidecars that are in-process MCP servers, to check when the router keeps or replaces a sidecar's connection.</summary>
public class ContainerRouterRecoveryTests
{
    private readonly List<Sidecar> _sidecars = [];

    [TearDown]
    public async Task TearDown()
    {
        foreach (Sidecar sidecar in _sidecars)
            await sidecar.DisposeAsync();
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Keeps_the_connection_when_one_call_fails(CancellationToken cancellationToken)
    {
        ContainerRouter router = new((_, _) => StartSidecarAsync());
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));

        Assert.That(async () => await route(Call("missing_tool"), cancellationToken), Throws.InstanceOf<McpException>());
        CallToolResult result = await route(Call("echo"), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.Not.True);
            Assert.That(_sidecars, Has.Count.EqualTo(1), "the failed call left the connection for the next one");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Replaces_a_closed_connection_before_the_next_call(CancellationToken cancellationToken)
    {
        ContainerRouter router = new((_, _) => StartSidecarAsync());
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));
        await route(Call("echo"), cancellationToken);

        await _sidecars[0].StopServerAsync();
        await _sidecars[0].Client.Completion.WaitAsync(cancellationToken);
        CallToolResult result = await route(Call("echo"), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.Not.True);
            Assert.That(_sidecars, Has.Count.EqualTo(2), "the closed connection was replaced by a new one");
        }
    }

    private RequestContext<CallToolRequestParams> Call(string tool) =>
        new(_sidecars.FirstOrDefault()?.Server ?? Sidecar.Placeholder, new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = tool, Arguments = new Dictionary<string, JsonElement> { [ContainerRouter.ContainerArgument] = JsonSerializer.SerializeToElement("node") } });

    private async Task<McpClient> StartSidecarAsync()
    {
        Sidecar sidecar = await Sidecar.StartAsync();
        _sidecars.Add(sidecar);
        return sidecar.Client;
    }

    private sealed class Sidecar : IAsyncDisposable
    {
        /// <summary>A server for request contexts made before any sidecar started; the router does not use it.</summary>
        public static McpServer Placeholder { get; } = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());

        private readonly CancellationTokenSource _stop = new();
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();
        private Task _running = Task.CompletedTask;

        public McpServer Server { get; private set; } = null!;
        public McpClient Client { get; private set; } = null!;

        public static async Task<Sidecar> StartAsync()
        {
            Sidecar sidecar = new();
            sidecar.Server = McpServer.Create(new StreamServerTransport(sidecar._toServer.Reader.AsStream(), sidecar._toClient.Writer.AsStream()),
                new McpServerOptions { ToolCollection = [McpServerTool.Create(static () => "ok", new McpServerToolCreateOptions { Name = "echo" })] });
            sidecar._running = sidecar.Server.RunAsync(sidecar._stop.Token);
            sidecar.Client = await McpClient.CreateAsync(new StreamClientTransport(sidecar._toServer.Writer.AsStream(), sidecar._toClient.Reader.AsStream()));
            return sidecar;
        }

        /// <summary>Ends the server and closes its side of the connection, as a sidecar does when its container stops.</summary>
        public async Task StopServerAsync()
        {
            await _toClient.Writer.CompleteAsync();
            try
            {
                await _stop.CancelAsync();
                await _running;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // How the SDK's server ends when its run is cancelled.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopServerAsync();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            _stop.Dispose();
        }
    }
}
