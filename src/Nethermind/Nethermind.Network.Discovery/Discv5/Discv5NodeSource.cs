// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

public class Discv5NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    KademliaConfig<Node> kademliaConfig,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private readonly ILogger _logger = logManager.GetClassLogger<Discv5NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discv5 node source");

        Channel<Node> channel = Channel.CreateBounded<Node>(64);
        ConcurrentDictionary<Hash256, Hash256> writtenNodes = new();
        int initialNodes = 0;

        foreach (Node node in kademlia.IterateNodes())
        {
            if (!IsExcluded(node) && writtenNodes.TryAdd(node.IdHash, node.IdHash))
            {
                initialNodes++;
                yield return node;
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Discv5 node source emitted {initialNodes} initial nodes from the routing table.");

        kademlia.OnNodeAdded += Handler;
        try
        {
            await foreach (Node node in channel.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            kademlia.OnNodeAdded -= Handler;
        }

        void Handler(object? _, Node node)
        {
            if (!IsExcluded(node) && writtenNodes.TryAdd(node.IdHash, node.IdHash))
            {
                if (channel.Writer.TryWrite(node))
                {
                    if (_logger.IsDebug) _logger.Debug($"Discv5 node source queued discovered node {node:s}.");
                }
                else if (_logger.IsTrace)
                {
                    _logger.Trace($"Discv5 node source queue is full, dropping discovered node {node:s}.");
                }
            }
        }
    }

    private bool IsExcluded(Node node) => node.IsBootnode || node.IdHash.Equals(_currentNodeHash);
}
