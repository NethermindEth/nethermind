// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

public class Discv5NodeSource(
    IKademlia<PublicKey, Node> kademlia,
    KademliaConfig<Node> kademliaConfig,
    ILogManager logManager)
    : IKademliaNodeSource
{
    private const int ChannelCapacity = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<Discv5NodeSource>();
    private readonly Hash256 _currentNodeHash = kademliaConfig.CurrentNodeId.IdHash;
    private readonly int _recentNodeLimit = Math.Max(ChannelCapacity, kademliaConfig.KSize * Hash256KademliaDistance.Instance.MaxDistance);

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discv5 node source");

        Channel<Node> channel = Channel.CreateBounded<Node>(ChannelCapacity);
        LinkedList<Hash256> recentlyWrittenNodes = [];
        Dictionary<Hash256, LinkedListNode<Hash256>> writtenNodes = [];
        object writtenNodesLock = new();
        int initialNodes = 0;

        foreach (Node node in kademlia.IterateNodes())
        {
            if (!IsExcluded(node) && TryReserveNode(node.IdHash))
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
            if (IsExcluded(node) || !TryReserveNode(node.IdHash))
            {
                return;
            }

            if (channel.Writer.TryWrite(node))
            {
                if (_logger.IsDebug) _logger.Debug($"Discv5 node source queued discovered node {node:s}.");
                return;
            }

            ReleaseReservedNode(node.IdHash);
            if (_logger.IsTrace)
            {
                _logger.Trace($"Discv5 node source queue is full, dropping discovered node {node:s}.");
            }
        }

        bool TryReserveNode(Hash256 nodeId)
        {
            lock (writtenNodesLock)
            {
                if (writtenNodes.ContainsKey(nodeId))
                {
                    return false;
                }

                LinkedListNode<Hash256> listNode = recentlyWrittenNodes.AddLast(nodeId);
                writtenNodes.Add(nodeId, listNode);
                while (writtenNodes.Count > _recentNodeLimit)
                {
                    LinkedListNode<Hash256> oldestNode = recentlyWrittenNodes.First!;
                    recentlyWrittenNodes.RemoveFirst();
                    writtenNodes.Remove(oldestNode.Value);
                }

                return true;
            }
        }

        void ReleaseReservedNode(Hash256 nodeId)
        {
            lock (writtenNodesLock)
            {
                if (writtenNodes.Remove(nodeId, out LinkedListNode<Hash256>? listNode))
                {
                    recentlyWrittenNodes.Remove(listNode);
                }
            }
        }
    }

    private bool IsExcluded(Node node) => node.IsBootnode || node.IdHash.Equals(_currentNodeHash);
}
