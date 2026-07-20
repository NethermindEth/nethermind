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

    /// <summary>Forwards a detection request body to the sibling's /portfolio-detect, so the multi-chain
    /// view can drive historical detection on sibling chains (not just the connected one).</summary>
    Task ProxyDetectAsync(int port, Stream requestBody, Stream responseBody, CancellationToken cancellationToken);
}

public readonly record struct SiblingNode(int Port, string ChainId);

public sealed class SiblingNodeRegistry : ISiblingNodeRegistry, IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    // a sibling that answered recently is kept through transient probe failures
    // (e.g. timeouts while the host is saturated by sync) instead of flapping away
    private static readonly TimeSpan SiblingGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly byte[] ChainIdRequest = Encoding.UTF8.GetBytes(
        """{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""");

    // Proxied JSON-RPC can be slow (e.g. a batch of on-chain NFT tokenURIs that render SVGs), and with
    // ResponseHeadersRead the sibling computes the whole batch before headers arrive — so the client timeout
    // must cover that. Probes bound themselves to ProbeTimeout via a linked token instead.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(100) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly int[] _probePorts;
    private readonly ILogger _logger;
    private readonly Dictionary<int, (SiblingNode Node, DateTimeOffset LastSeen)> _lastSeen = [];

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
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (SiblingNode? sibling in probed)
            {
                if (sibling is not null) _lastSeen[sibling.Value.Port] = (sibling.Value, now);
            }

            foreach (int port in _lastSeen.Keys.Where(p => now - _lastSeen[p].LastSeen > SiblingGracePeriod).ToArray())
            {
                _lastSeen.Remove(port);
            }

            _siblings = _lastSeen.Values.Select(entry => entry.Node).OrderBy(s => s.Port).ToArray();
            _refreshedAt = now;
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

    public Task ProxyAsync(int port, Stream requestBody, Stream responseBody, CancellationToken cancellationToken) =>
        ForwardAsync($"http://127.0.0.1:{port}/", requestBody, responseBody, cancellationToken);

    public Task ProxyDetectAsync(int port, Stream requestBody, Stream responseBody, CancellationToken cancellationToken) =>
        ForwardAsync($"http://127.0.0.1:{port}/portfolio-detect", requestBody, responseBody, cancellationToken);

    private async Task ForwardAsync(string url, Stream requestBody, Stream responseBody, CancellationToken cancellationToken)
    {
        using HttpRequestMessage forward = new(HttpMethod.Post, url);
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
            using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(ProbeTimeout);
            using HttpResponseMessage response = await _client.SendAsync(request, probeCts.Token);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("result", out JsonElement result)) return null;

            string? chainId = result.GetString();
            return chainId is not null && chainId.StartsWith("0x") ? new SiblingNode(port, chainId) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // probe timed out (ProbeTimeout) — treat as no sibling, don't fault the refresh
            return null;
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
