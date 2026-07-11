// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Discovers sibling Nethermind JSON-RPC endpoints on localhost for the multi-chain balance view.</summary>
public interface ISiblingNodeRegistry
{
    /// <summary>Returns the currently discovered siblings, refreshing the cached probe results when stale.</summary>
    Task<IReadOnlyList<SiblingNode>> GetSiblingsAsync(CancellationToken cancellationToken);

    /// <summary>Whether the port belongs to a discovered sibling, based on the last completed probe.</summary>
    bool IsKnownSibling(int port);

    /// <summary>Forwards a JSON-RPC request body to the sibling on the given localhost port.</summary>
    Task ProxyAsync(int port, Stream requestBody, Stream responseBody, CancellationToken cancellationToken);
}

public readonly record struct SiblingNode(int Port, string ChainId);

public sealed class SiblingNodeRegistry : ISiblingNodeRegistry, IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly byte[] ChainIdRequest = Encoding.UTF8.GetBytes(
        """{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""");

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly int[] _probePorts;
    private readonly ILogger _logger;

    private volatile IReadOnlyList<SiblingNode> _siblings = [];
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public SiblingNodeRegistry(IBalanceViewerConfig config, IJsonRpcUrlCollection jsonRpcUrlCollection, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<SiblingNodeRegistry>();
        HashSet<int> ownPorts = [.. jsonRpcUrlCollection.Keys];
        _probePorts = config.SiblingProbePorts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out int port) ? port : 0)
            .Where(p => p > 0 && !ownPorts.Contains(p))
            .Distinct()
            .ToArray();
    }

    public async Task<IReadOnlyList<SiblingNode>> GetSiblingsAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _refreshedAt < CacheDuration) return _siblings;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _refreshedAt < CacheDuration) return _siblings;

            SiblingNode?[] probed = await Task.WhenAll(_probePorts.Select(p => ProbeAsync(p, cancellationToken)));
            _siblings = probed.Where(s => s is not null).Select(s => s!.Value).ToArray();
            _refreshedAt = DateTimeOffset.UtcNow;
            return _siblings;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public bool IsKnownSibling(int port)
    {
        foreach (SiblingNode sibling in _siblings)
        {
            if (sibling.Port == port) return true;
        }

        return false;
    }

    public async Task ProxyAsync(int port, Stream requestBody, Stream responseBody, CancellationToken cancellationToken)
    {
        using HttpRequestMessage forward = new(HttpMethod.Post, $"http://127.0.0.1:{port}/");
        forward.Content = new StreamContent(requestBody);
        forward.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.Content.CopyToAsync(responseBody, cancellationToken);
    }

    private async Task<SiblingNode?> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{port}/");
            request.Content = new ByteArrayContent(ChainIdRequest);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("result", out JsonElement result)) return null;

            string? chainId = result.GetString();
            return chainId is not null && chainId.StartsWith("0x") ? new SiblingNode(port, chainId) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"No sibling node on port {port}: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _refreshLock.Dispose();
    }
}
