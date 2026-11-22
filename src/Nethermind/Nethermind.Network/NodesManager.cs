// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public abstract class NodesManager(string path, ILogger logger)
{
    private static readonly char[] _separator = ['\r', '\n'];

    protected readonly ILogger _logger = logger;
    protected ConcurrentDictionary<PublicKey, NetworkNode> _nodes = [];

    // Refcount of managed nodes per IP so membership can be queried in O(1) on hot paths.
    // A count rather than a set because several nodes can share one IP (different ports/keys).
    // The comparer folds IPv4-mapped IPv6 onto plain IPv4 so both forms share a slot.
    private readonly ConcurrentDictionary<IPAddress, int> _ipCounts = new(NormalizingIpAddressComparer.Instance);

    // Guards the (_nodes, _ipCounts) pair so a node is never present in one but missing from the other.
    private readonly Lock _indexLock = new();

    /// <summary>Returns <see langword="true"/> when any managed node is reachable at <paramref name="ip"/>.</summary>
    public bool ContainsIp(IPAddress ip) => _ipCounts.ContainsKey(ip);

    // Nodes explicitly persisted (loaded from the file or added with updateFile=true). The file mirrors this set,
    // so transient (updateFile=false) changes to _nodes never leak into it and a failed load cannot be amplified.
    private ConcurrentDictionary<PublicKey, NetworkNode> _persistedNodes = [];

    // Subscribers of the running DiscoverNetworkNodes enumerations; each only does a non-blocking TryWrite.
    private Action<NetworkNode>? _nodeAdded;

    /// <summary>Adds a node and updates the IP index. Returns <see langword="false"/> if already present.</summary>
    protected bool TryAddNode(NetworkNode node)
    {
        lock (_indexLock)
        {
            if (!_nodes.TryAdd(node.NodeId, node)) return false;
            _ipCounts.AddOrUpdate(node.HostIp, 1, static (_, count) => count + 1);
            // Inside the lock, unlike NodeRemoved: enumerations copy _nodes under it, so each gets an add once; subscribers only TryWrite.
            _nodeAdded?.Invoke(node);
            return true;
        }
    }

    /// <summary>Yields each managed node once: the nodes present when the enumeration starts, then every node added later.</summary>
    protected async IAsyncEnumerable<NetworkNode> DiscoverNetworkNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<NetworkNode> added = Channel.CreateUnbounded<NetworkNode>();
        Action<NetworkNode> onNodeAdded = node => added.Writer.TryWrite(node);

        NetworkNode[] snapshot = Subscribe(onNodeAdded);
        try
        {
            foreach (NetworkNode node in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsManaged(node)) yield return node;
            }

            await foreach (NetworkNode node in added.Reader.ReadAllAsync(cancellationToken))
            {
                if (IsManaged(node)) yield return node;
            }
        }
        finally
        {
            Unsubscribe(onNodeAdded);
        }
    }

    // Under the lock that guards every add: a node is either in the returned copy or published to the subscriber.
    // Every writer of _nodes holds this lock, so walking the dictionary here gives a point-in-time copy.
    private NetworkNode[] Subscribe(Action<NetworkNode> onNodeAdded)
    {
        lock (_indexLock)
        {
            _nodeAdded += onNodeAdded;
            return [.. _nodes.Select(static kvp => kvp.Value)];
        }
    }

    private void Unsubscribe(Action<NetworkNode> onNodeAdded)
    {
        lock (_indexLock)
        {
            _nodeAdded -= onNodeAdded;
        }
    }

    // A node removed, or replaced by a later add, after it was copied or queued is not yielded.
    private bool IsManaged(NetworkNode node) => _nodes.TryGetValue(node.NodeId, out NetworkNode? current) && ReferenceEquals(current, node);

    /// <summary>Replaces the whole node set (bulk load) and rebuilds the IP index.</summary>
    protected void SetNodes(ConcurrentDictionary<PublicKey, NetworkNode> nodes)
    {
        lock (_indexLock)
        {
            _nodes = nodes;
            _ipCounts.Clear();
            foreach (KeyValuePair<PublicKey, NetworkNode> kvp in nodes)
            {
                _ipCounts.AddOrUpdate(kvp.Value.HostIp, 1, static (_, count) => count + 1);
            }

            _persistedNodes = new ConcurrentDictionary<PublicKey, NetworkNode>(nodes);
        }
    }

    private void UnindexIp(NetworkNode node)
    {
        IPAddress key = node.HostIp;
        while (_ipCounts.TryGetValue(key, out int count))
        {
            if (count > 1)
            {
                if (_ipCounts.TryUpdate(key, count - 1, count)) return;
            }
            else if (_ipCounts.TryRemove(new KeyValuePair<IPAddress, int>(key, count)))
            {
                return;
            }
        }
    }

    private void EnsureFile(string resource)
    {
        if (File.Exists(path))
        {
            return;
        }
        else // For backward compatibility. To be removed in future versions.
        {
            string oldPath = Path.GetFullPath($"Data/{resource}".GetApplicationResourcePath());

            if (File.Exists(oldPath))
            {
                bool moved = true;

                try
                {
                    File.Move(oldPath, path, false);
                }
                catch (Exception ex)
                {
                    moved = false;

                    if (_logger.IsWarn)
                        _logger.Warn($"Failed to move {oldPath:hide} to {Path.GetFullPath(path):hide}: {ex.Message:hide}\n  {resource} is ignored and will not be used");
                }

                if (moved)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"{oldPath:hide} has been moved to {Path.GetFullPath(path):hide}");

                    return;
                }
            }
        }

        // Create the directory if needed; a bare file name has none
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (_logger.IsDebug) _logger.Debug($"Nodes file was not found, creating one at {Path.GetFullPath(path):hide}");

        using Stream actualNodes = File.Create(path);
        using Stream embeddedNodes = typeof(NodesManager).Assembly.GetManifestResourceStream(resource);

        if (embeddedNodes is null)
        {
            if (_logger.IsDebug) _logger.Debug($"Embedded resource {resource} was not found");

            File.WriteAllText(path, "[]\n");
        }
        else
        {
            embeddedNodes.CopyTo(actualNodes);
        }
    }

    protected virtual void LogNodeList(string title, IDictionary<PublicKey, NetworkNode> nodes)
    {
        if (_logger.IsDebug && nodes.Count != 0)
        {
            string separator = $"{Environment.NewLine}  ";

            _logger.Debug($"{title}:{separator}{string.Join(separator, nodes.Values.Select(n => n.ToString())):hide}");
        }
    }

    protected virtual async Task<ConcurrentDictionary<PublicKey, NetworkNode>> ParseNodes(string fallbackResource)
    {
        EnsureFile(fallbackResource);

        string data = await File.ReadAllTextAsync(path);

        IEnumerable<string>? rawNodes;

        try
        {
            rawNodes = JsonSerializer.Deserialize(data, NodesJsonContext.Default.HashSetString);
        }
        catch (JsonException)
        {
            rawNodes = data.Split(_separator, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }

        ConcurrentDictionary<PublicKey, NetworkNode> nodes = [];

        foreach (string? n in rawNodes ?? [])
        {
            NetworkNode node;

            try
            {
                node = new(n);
            }
            catch (ArgumentException ex)
            {
                if (_logger.IsError) _logger.Error($"Failed to parse node: {n:hide}", SensitiveLogMasking.Enabled ? null : ex);

                continue;
            }

            nodes.TryAdd(node.NodeId, node);
        }

        if (_logger.IsInfo)
            _logger.Info($"Loaded {nodes.Count} nodes from {Path.GetFullPath(path):hide}");

        return nodes;
    }

    /// <summary>
    /// Raised when a node is explicitly removed from this source.
    /// </summary>
    public event EventHandler<NodeEventArgs>? NodeRemoved;

    /// <summary>
    /// Removes a node from the in-memory store and fires <see cref="NodeRemoved"/> as an
    /// <see cref="ExplicitNodeRemovalEventArgs"/> so downstream listeners (e.g. <c>PeerPool</c>)
    /// know to disconnect the peer unconditionally.
    /// </summary>
    protected bool TryRemoveNode(PublicKey nodeId)
    {
        NetworkNode? removed;
        lock (_indexLock)
        {
            if (!_nodes.TryRemove(nodeId, out removed))
                return false;

            UnindexIp(removed);
        }

        // Fire outside the lock so downstream handlers (e.g. PeerPool) don't run under it.
        NodeRemoved?.Invoke(this, new ExplicitNodeRemovalEventArgs(new Node(removed)));
        return true;
    }

    /// <summary>Marks <paramref name="node"/> persistent when <paramref name="updateFile"/> is set, rewriting the nodes file if it was not persisted yet, and returns <paramref name="changed"/>.</summary>
    protected async Task<bool> PersistAsync(bool changed, NetworkNode node, bool updateFile, CancellationToken cancellationToken)
    {
        if (updateFile && _persistedNodes.TryAdd(node.NodeId, node)) await SaveFileAsync(cancellationToken);
        return changed;
    }

    /// <summary>Unmarks <paramref name="node"/> as persistent when <paramref name="updateFile"/> is set, rewriting the nodes file if it was persisted, and returns <paramref name="changed"/>.</summary>
    protected async Task<bool> UnpersistAsync(bool changed, NetworkNode node, bool updateFile, CancellationToken cancellationToken)
    {
        if (updateFile && _persistedNodes.TryRemove(node.NodeId, out _)) await SaveFileAsync(cancellationToken);
        return changed;
    }

    protected virtual Task SaveFileAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string contents = TypeInfoJsonSerializer.Serialize(
            _persistedNodes.Select(static n => n.Value.ToString()),
            EthereumJsonSerializer.JsonOptionsIndented
            );

        return File.WriteAllTextAsync(path, contents, cancellationToken);
    }
}

[JsonSerializable(typeof(HashSet<string>))]
internal partial class NodesJsonContext : JsonSerializerContext;
