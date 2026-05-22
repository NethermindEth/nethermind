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

        foreach (Node node in kademlia.IterateNodes())
        {
            if (!IsSelf(node) && writtenNodes.TryAdd(node.IdHash, node.IdHash))
            {
                yield return node;
            }
        }

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
            if (!IsSelf(node) && writtenNodes.TryAdd(node.IdHash, node.IdHash))
            {
                channel.Writer.TryWrite(node);
            }
        }
    }

    private bool IsSelf(Node node) => node.IdHash.Equals(_currentNodeHash);
}
