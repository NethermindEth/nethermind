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
        _sidecars.Clear();
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

    [Test]
    [CancelAfter(30_000)]
    public async Task Keeps_connections_per_container_id(CancellationToken cancellationToken)
    {
        // "node" and "node-alias" name the first container; then "node" is taken by a new container.
        string nodeId = "id-1";
        ContainerRouter router = new((_, _) => StartSidecarAsync(), identify: (_, container, _) => Task.FromResult(container == "node" ? nodeId : "id-1"));
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));

        await route(Call("echo", "node"), cancellationToken);
        await route(Call("echo", "node-alias"), cancellationToken);
        int afterAlias = _sidecars.Count;
        nodeId = "id-2";
        await route(Call("echo", "node"), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterAlias, Is.EqualTo(1), "a name and an alias of one container share its connection");
            Assert.That(_sidecars, Has.Count.EqualTo(2), "a name now held by another container gets that container's sidecar");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Writes_a_dump_even_when_its_dac_cannot_be_supplied(CancellationToken cancellationToken)
    {
        ContainerRouter router = new((_, _) => StartSidecarAsync(), provideDebuggingLibraries: static (_, _, _) => throw new InvalidOperationException("nuget.org is unreachable"));
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));

        CallToolResult dump = await route(Call("write_dump"), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dump.IsError, Is.Not.True);
            Assert.That(dump.Content.OfType<TextContentBlock>().Select(static c => c.Text), Has.Some.EqualTo("ok").And.Some.Contains("nuget.org is unreachable"));
            Assert.That(async () => await route(Call("native_memory"), cancellationToken), Throws.InvalidOperationException, "a tool that loads the DAC itself still fails");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Forgets_a_connection_once_it_closes(CancellationToken cancellationToken)
    {
        ContainerRouter router = new((_, _) => StartSidecarAsync());
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));
        await route(Call("echo"), cancellationToken);
        int connected = router.SidecarCount;

        // As when the container stops; a replacement has another ID, so no later call would look the old entry up.
        await _sidecars[0].StopServerAsync();
        while (router.SidecarCount > 0)
            await Task.Delay(20, cancellationToken);

        Assert.That(connected, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Disposes_a_sidecar_that_finishes_starting_during_disposal(CancellationToken cancellationToken)
    {
        TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ContainerRouter router = new(async (_, _) =>
        {
            await startup.Task;
            return await StartSidecarAsync();
        });
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));

        using CancellationTokenSource caller = new();
        Task call = route(Call("echo"), caller.Token).AsTask();
        await caller.CancelAsync();
        Assert.That(async () => await call, Throws.InstanceOf<OperationCanceledException>(), "the caller stops waiting while the start goes on");

        Task disposal = router.DisposeAsync().AsTask();
        startup.SetResult();
        await disposal.WaitAsync(cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sidecars, Has.Count.EqualTo(1));
            Assert.That(_sidecars[0].Client.Completion.IsCompleted, Is.True, "the client that connected during disposal was disposed");
            Assert.That(async () => await route(Call("echo"), cancellationToken), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Disposes_a_running_sidecar_that_is_looked_up_during_disposal(CancellationToken cancellationToken)
    {
        // The race is narrow, so it is run many times.
        for (int i = 0; i < 50; i++)
        {
            ContainerRouter router = new((_, _) => StartSidecarAsync());
            await (await router.LiveSidecarAsync((null, "node"))).Value;
            TaskCompletionSource looking = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task lookups = Task.Run(async () =>
            {
                looking.SetResult();
                try
                {
                    while (true)
                        await router.LiveSidecarAsync((null, "node"));
                }
                catch (ObjectDisposedException)
                {
                }
            }, cancellationToken);

            await looking.Task;
            await router.DisposeAsync();
            await lookups.WaitAsync(cancellationToken);

            Assert.That(_sidecars[^1].Client.Completion.IsCompleted, Is.True, $"run {i}: the sidecar was disposed");
        }
    }

    [TestCase("write_dump", false, true)]
    [TestCase("write_dump", true, false)]
    [TestCase("native_memory", false, true)]
    [TestCase("native_memory", true, true)]
    [CancelAfter(30_000)]
    public async Task Reports_dacs_that_could_not_be_supplied(string tool, bool toolFails, bool warned, CancellationToken cancellationToken)
    {
        ContainerRouter router = new((_, _) => StartSidecarAsync(), provideDebuggingLibraries: static (_, _, _) => Task.FromResult<string?>(".NET 9.0.1: nuget.org is unreachable"));
        McpRequestHandler<CallToolRequestParams, CallToolResult> route = router.Route(static (_, _) => throw new AssertionException("the call should be routed"));

        CallToolResult result = await route(Call(tool, fail: toolFails), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError == true, Is.EqualTo(toolFails));
            Assert.That(result.Content.OfType<TextContentBlock>().Select(static c => c.Text), warned ? Has.Some.Contains("nuget.org is unreachable") : Has.None.Contains("nuget.org"));
        }
    }

    private RequestContext<CallToolRequestParams> Call(string tool, string container = "node", bool fail = false)
    {
        Dictionary<string, JsonElement> arguments = new() { [ContainerRouter.ContainerArgument] = JsonSerializer.SerializeToElement(container) };
        if (fail)
            arguments["fail"] = JsonSerializer.SerializeToElement(true);
        return new(_sidecars.FirstOrDefault()?.Server ?? Sidecar.Placeholder, new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = tool, Arguments = arguments });
    }

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
                new McpServerOptions
                {
                    ToolCollection =
                    [
                        McpServerTool.Create(static () => "ok", new McpServerToolCreateOptions { Name = "echo" }),
                        McpServerTool.Create(static (bool fail = false) => fail ? throw new InvalidOperationException("disk full") : "ok", new McpServerToolCreateOptions { Name = "write_dump" }),
                        McpServerTool.Create(static (bool fail = false) => fail ? throw new InvalidOperationException("no DAC") : "ok", new McpServerToolCreateOptions { Name = "native_memory" }),
                    ],
                });
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
