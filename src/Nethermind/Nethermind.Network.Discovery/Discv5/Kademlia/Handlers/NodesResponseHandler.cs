// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class NodesResponseHandler(
    Node receiver,
    Distances requestedDistances,
    IKademliaDistance<ValueHash256> distanceCalculator,
    IPAddress localIp)
    : ResponseHandler<NodesMsg>(MessageType.Nodes), IDisposable
{
    private const int MaxNodesResponseMessages = 16;
    private const int MaxNodesResponseRecords = 64;
    private const int SeenNodeIdsCapacity = 128;
    private const int SeenNodeIdsMask = SeenNodeIdsCapacity - 1;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Node[] _nodes = new Node[MaxNodesResponseRecords];
    private readonly CompressedPublicKey?[] _seenNodeIds = new CompressedPublicKey?[SeenNodeIdsCapacity];
    private readonly bool _allowNonRoutableRelays = receiver.DiscoveryAddress.Address.IsLoopbackOrPrivateOrLinkLocal;

    private readonly Lock _lock = new();
    private bool _done;
    private int _totalMessages;
    private int _receivedMessages;
    private int _nodeCount;

    public override Task Task => _completion.Task;

    public void Dispose()
    {
        lock (_lock)
        {
            _done = true;
        }
    }

    public override bool Handle(NodesMsg nodes)
    {
        if (nodes.Total <= 0 || nodes.Total > MaxNodesResponseMessages)
        {
            Complete();
            return true;
        }

        bool complete = false;

        lock (_lock)
        {
            if (_done)
            {
                return true;
            }

            if (_totalMessages != 0 && _totalMessages != nodes.Total)
            {
                complete = CompleteLocked();
            }
            else
            {
                _totalMessages = nodes.Total;
                _receivedMessages++;

                if (_receivedMessages <= nodes.Total)
                {
                    AddRecords(nodes);
                }

                if (_receivedMessages >= nodes.Total || _nodeCount >= MaxNodesResponseRecords)
                {
                    complete = CompleteLocked();
                }
            }
        }

        if (complete)
        {
            _completion.TrySetResult();
        }

        return true;
    }

    public Node[] GetNodes()
    {
        if (!Task.IsCompleted)
        {
            throw new InvalidOperationException($"{nameof(GetNodes)} must be called after the response handler completes.");
        }

        int nodeCount = _nodeCount;
        if (nodeCount == 0)
        {
            return [];
        }

        Node[] nodes = _nodes;
        if (nodeCount != nodes.Length)
        {
            Array.Resize(ref nodes, nodeCount);
        }

        return nodes;
    }

    private void AddRecords(NodesMsg nodes)
    {
        for (int i = 0; i < nodes.Records.Count && _nodeCount < MaxNodesResponseRecords; i++)
        {
            NodeRecord record = nodes.Records[i];
            if (!KademliaAdapter.TryGetAcceptableDiscoveryEndpoint(record, _allowNonRoutableRelays, localIp, preferredEndpoint: null, out IPEndPoint? endpoint))
            {
                continue;
            }

            CompressedPublicKey? compressedKey = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
            if (compressedKey is null || !TryMarkSeen(compressedKey))
            {
                continue;
            }

            PublicKey key = compressedKey.Decompress();
            if (!MatchesRequestedDistance(key.Hash, requestedDistances))
            {
                continue;
            }

            Node node = Node.FromDiscoveryEnr(record, key, endpoint);
            node.SetVerifiedEnr(record);
            _nodes[_nodeCount++] = node;
        }
    }

    private bool TryMarkSeen(CompressedPublicKey nodeId)
    {
        // Skip the compressed key's 0x02/0x03 prefix when choosing a bucket.
        int hash = nodeId.GetHashCode() >>> 8;
        for (int i = 0; i < SeenNodeIdsCapacity; i++)
        {
            int index = (hash + i) & SeenNodeIdsMask;
            CompressedPublicKey? current = _seenNodeIds[index];
            if (current is null)
            {
                _seenNodeIds[index] = nodeId;
                return true;
            }

            if (current.Equals(nodeId))
            {
                return false;
            }
        }

        return false;
    }

    private void Complete()
    {
        bool complete;
        lock (_lock)
        {
            complete = CompleteLocked();
        }

        if (complete)
        {
            _completion.TrySetResult();
        }
    }

    private bool CompleteLocked()
    {
        if (_done)
        {
            return false;
        }

        _done = true;
        return true;
    }

    private bool MatchesRequestedDistance(Hash256 nodeId, Distances requestedDistances)
    {
        int distance = distanceCalculator.CalculateLogDistance(receiver.Id.Hash.ValueHash256, nodeId.ValueHash256);
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
