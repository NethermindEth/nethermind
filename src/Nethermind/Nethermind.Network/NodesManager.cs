// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nethermind.Network;

public abstract class NodesManager(string path, ILogger logger)
{
    private static readonly char[] _separator = ['\r', '\n'];

    protected readonly ILogger _logger = logger;
    protected ConcurrentDictionary<PublicKey, NetworkNode> _nodes = [];

    private void EnsureFile(string resource)
    {
        if (File.Exists(path))
        {
            return;
        }
        else // For backward compatibility. To be removed in future versions.
        {
            string oldPath = Path.GetFullPath(string.Empty.GetApplicationResourcePath($"Data/{resource}"));

            if (File.Exists(oldPath))
            {
                var moved = true;

                try
                {
                    File.Move(oldPath, path, false);
                }
                catch (Exception ex)
                {
                    moved = false;

                    if (_logger.IsWarn)
                        _logger.Warn($"Failed to move {oldPath} to {Path.GetFullPath(path)}: {ex.Message}\n  {resource} is ignored and will not be used");
                }

                if (moved)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"{oldPath} has been moved to {Path.GetFullPath(path)}");

                    return;
                }
            }
        }

        // Create the directory if needed
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        if (_logger.IsDebug) _logger.Debug($"Nodes file was not found, creating one at {Path.GetFullPath(path)}");

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
            var separator = $"{Environment.NewLine}  ";

            _logger.Debug($"{title}:{separator}{string.Join(separator, nodes.Values.Select(n => n.ToString()))}");
        }
    }

    protected virtual async Task<ConcurrentDictionary<PublicKey, NetworkNode>> ParseNodes(string fallbackResource)
    {
        EnsureFile(fallbackResource);

        string data = await File.ReadAllTextAsync(path);

        IEnumerable<string>? rawNodes;

        try
        {
            rawNodes = JsonSerializer.Deserialize<HashSet<string>>(data);
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
                if (_logger.IsError) _logger.Error($"Failed to parse node: {n}", ex);

                continue;
            }

            nodes.TryAdd(node.NodeId, node);
        }

        if (_logger.IsInfo)
            _logger.Info($"Loaded {nodes.Count} nodes from {Path.GetFullPath(path)}");

        return nodes;
    }

    protected virtual Task SaveFileAsync()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string contents = JsonSerializer.Serialize(
            _nodes.Select(static n => n.Value.ToString()),
            EthereumJsonSerializer.JsonOptionsIndented
            );

        return File.WriteAllTextAsync(path, contents);
    }
}
