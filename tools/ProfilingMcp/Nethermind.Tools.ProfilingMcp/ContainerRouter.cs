// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>
/// Lets every tool profile the processes of a Docker container: a call that names one is answered by a sidecar for that container.
/// </summary>
/// <remarks>
/// Each tool gets two optional arguments, the container and the engine running it, so the agent picks the target per call,
/// e.g. from "profile my local hoodi node". The first call for a container starts its sidecar (<see cref="ContainerSidecar"/>),
/// which serves the same tools for the container's processes; later calls reuse it until this server exits. Files the sidecar writes
/// stay there, so analysis tools are given the same container to read a trace or snapshot taken in it.
/// </remarks>
internal sealed class ContainerRouter : IAsyncDisposable
{
    public const string ContainerArgument = "container";
    public const string DockerHostArgument = "dockerHost";

    private const string ContainerDescription =
        "Name or ID of a Docker container whose processes to profile, from list_containers. PIDs and file paths then refer to that container's sidecar: " +
        "pass the same container to analyze a trace or snapshot taken there. Omit for processes and files on this machine.";

    private const string DockerHostDescription =
        "Docker engine running the container: a context name from list_containers, or an address such as ssh://user@host or tcp://host:2376. " +
        "Omit for the engine the docker CLI uses by default.";

    private static readonly HashSet<string> Unrouted = ["list_containers"];

    private readonly ConcurrentDictionary<(string? DockerHost, string Container), Lazy<Task<McpClient>>> _sidecars = new();
    private readonly Func<string?, string, Task<McpClient>> _start;

    public ContainerRouter() : this(ContainerSidecar.StartAsync)
    {
    }

    /// <param name="start">Starts a sidecar for an engine and container and connects to it.</param>
    internal ContainerRouter(Func<string?, string, Task<McpClient>> start) => _start = start;

    /// <summary>Adds the container and engine arguments to the tools' input schemas.</summary>
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> AddTargetArguments(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, cancellationToken) =>
        {
            ListToolsResult result = await next(request, cancellationToken);
            foreach (Tool tool in result.Tools)
            {
                if (!Unrouted.Contains(tool.Name))
                    tool.InputSchema = WithTargetArguments(tool.InputSchema);
            }

            return result;
        };

    internal static JsonElement WithTargetArguments(JsonElement schema)
    {
        JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (root["properties"] is not JsonObject properties)
            root["properties"] = properties = [];

        properties[ContainerArgument] = new JsonObject { ["type"] = "string", ["description"] = ContainerDescription };
        properties[DockerHostArgument] = new JsonObject { ["type"] = "string", ["description"] = DockerHostDescription };
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Sends a call that names a container to that container's sidecar, starting it on first use.</summary>
    public McpRequestHandler<CallToolRequestParams, CallToolResult> Route(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (request.Params is not { } call || !TryTakeTarget(call.Arguments, out string? dockerHost, out string? container, out Dictionary<string, JsonElement> arguments))
                return await next(request, cancellationToken);

            (string?, string) key = (dockerHost, container!);
            Lazy<Task<McpClient>> sidecar = await LiveSidecarAsync(key);
            try
            {
                McpClient client = await sidecar.Value.WaitAsync(cancellationToken);
                return await client.CallToolAsync(new CallToolRequestParams { Name = call.Name, Arguments = arguments }, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException && IsGone(sidecar.Value))
            {
                // A sidecar that failed to start, or ended with its container, is started afresh by the next call.
                await DropAsync(key, sidecar);
                throw;
            }
        };

    /// <summary>Returns the sidecar for a target, replacing one known to be gone, such as after its target restarted, before it fails a call.</summary>
    internal async Task<Lazy<Task<McpClient>>> LiveSidecarAsync((string? DockerHost, string Container) key)
    {
        Lazy<Task<McpClient>> sidecar = Add(key);
        if (!sidecar.IsValueCreated || !IsGone(sidecar.Value))
            return sidecar;

        await DropAsync(key, sidecar);
        return Add(key);
    }

    private Lazy<Task<McpClient>> Add((string? DockerHost, string Container) key) =>
        _sidecars.GetOrAdd(key, key => new(() => _start(key.DockerHost, key.Container)));

    private async Task DropAsync((string?, string) key, Lazy<Task<McpClient>> sidecar)
    {
        if (_sidecars.TryRemove(new KeyValuePair<(string?, string), Lazy<Task<McpClient>>>(key, sidecar)) && sidecar.Value.IsCompletedSuccessfully)
            await sidecar.Value.Result.DisposeAsync();
    }

    /// <summary>Whether a sidecar failed to start or its connection closed, as opposed to one call failing while others still use it.</summary>
    internal static bool IsGone(Task<McpClient> sidecar) =>
        sidecar.IsCompleted && (!sidecar.IsCompletedSuccessfully || sidecar.Result.Completion.IsCompleted);

    /// <summary>Splits the container and engine arguments from the ones for the tool.</summary>
    /// <returns>Whether the call names a container.</returns>
    internal static bool TryTakeTarget(IDictionary<string, JsonElement>? arguments, out string? dockerHost, out string? container, out Dictionary<string, JsonElement> rest)
    {
        rest = arguments is null ? [] : new Dictionary<string, JsonElement>(arguments);
        container = rest.Remove(ContainerArgument, out JsonElement value) ? Text(value) : null;
        dockerHost = rest.Remove(DockerHostArgument, out value) ? Text(value) : null;
        return container is not null;

        static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<Task<McpClient>> sidecar in _sidecars.Values)
        {
            if (sidecar.IsValueCreated && sidecar.Value.IsCompletedSuccessfully)
                await sidecar.Value.Result.DisposeAsync();
        }
    }
}
