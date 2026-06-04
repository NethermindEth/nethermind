// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class NodesResponseHandler(Node receiver, Distances requestedDistances, IKademliaDistance<Hash256> distanceCalculator)
    : ResponseHandler<NodesMsg>(MessageType.Nodes)
{
    private const int MaxNodesResponseMessages = 16;
    private const int MaxNodesResponseRecords = 64;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Node> _nodes = [];
    private readonly HashSet<Hash256> _seenNodeIds = [];
    private readonly bool _allowNonRoutableRelays = IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(receiver.Address.Address);
    private int? _total;
    private int _received;

    public override Task Task => _completion.Task;

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

        for (int i = 0; i < nodes.Records.Count && _nodes.Count < MaxNodesResponseRecords; i++)
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

            _nodes.Add(node);
        }

        if (_received >= _total || _nodes.Count >= MaxNodesResponseRecords)
        {
            _completion.TrySetResult();
        }

        return true;
    }

    public Node[] GetNodes() => [.. _nodes];

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
