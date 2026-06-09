// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Collections.Pooled;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class NodesResponseHandler(Node receiver, Distances requestedDistances, IKademliaDistance<Hash256> distanceCalculator)
    : ResponseHandler<NodesMsg>(MessageType.Nodes), IDisposable
{
    private const int MaxNodesResponseMessages = 16;
    private const int MaxNodesResponseRecords = 64;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Node[] _nodes = new Node[MaxNodesResponseRecords];
    private readonly PooledSet<Hash256> _seenNodeIds = new(MaxNodesResponseRecords);
    private readonly bool _allowNonRoutableRelays = IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(receiver.Address.Address);
    private int? _total;
    private int _received;
    private int _nodeCount;

    public override Task Task => _completion.Task;

    public void Dispose() => _seenNodeIds.Dispose();

    public override bool Handle(NodesMsg nodes)
    {
        if (_completion.Task.IsCompleted)
        {
            return true;
        }

        if (nodes.Total <= 0 || nodes.Total > MaxNodesResponseMessages)
        {
            _completion.TrySetResult();
            return true;
        }

        if (_total is not null && _total.Value != nodes.Total)
        {
            _completion.TrySetResult();
            return true;
        }

        _total ??= nodes.Total;
        _received++;

        for (int i = 0; i < nodes.Records.Count && _nodeCount < MaxNodesResponseRecords; i++)
        {
            NodeRecord record = nodes.Records[i];
            if (DiscoveryV5App.IsConsensusOnlyNodeRecord(record) ||
                !Node.TryFromDiscoveryEnr(record, out Node? node) ||
                !DiscoveryV5App.IsDiscoveryAddressAcceptable(node.Address.Address, _allowNonRoutableRelays) ||
                !_seenNodeIds.Add(node.Id.Hash) ||
                !MatchesRequestedDistance(node, requestedDistances))
            {
                continue;
            }

            _nodes[_nodeCount++] = node;
        }

        if (_received >= _total || _nodeCount >= MaxNodesResponseRecords)
        {
            _completion.TrySetResult();
        }

        return true;
    }

    public Node[] GetNodes()
    {
        if (_nodeCount == 0)
        {
            return [];
        }

        Node[] nodes = new Node[_nodeCount];
        Array.Copy(_nodes, nodes, _nodeCount);
        return nodes;
    }

    private bool MatchesRequestedDistance(Node node, Distances requestedDistances)
    {
        int distance = distanceCalculator.CalculateLogDistance(receiver.Id.Hash, node.Id.Hash);
        for (int i = 0; i < requestedDistances.Count; i++)
        {
            if (requestedDistances[i] == distance)
            {
                return true;
            }
        }

        return false;
    }
}
