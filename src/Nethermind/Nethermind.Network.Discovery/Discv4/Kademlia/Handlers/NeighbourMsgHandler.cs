// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

public sealed class NeighbourMsgHandler(int k, IPAddress localIp) : ITaskCompleter<Node[]>
{
    private readonly Lock _lock = new();
    private readonly Node[] _nodes = new Node[k];
    private int _count;
    public TaskCompletionSource<DiscoveryResponse<Node[]>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The peer should send the two packet pretty much immediately. In any case, if the second packet is loss, its not a huge deal.
    private static readonly TimeSpan _secondRequestTimeout = TimeSpan.FromMilliseconds(100);
    private int _timeoutInitiated;

    public bool Handle(DiscoveryMsg msg)
    {
        if (TaskCompletionSource.Task.IsCompleted) return false;

        NeighborsMsg neighborsMsg = (NeighborsMsg)msg;
        bool isComplete;

        lock (_lock)
        {
            if (TaskCompletionSource.Task.IsCompleted) return false;

            if (_count >= k) return false;

            for (int i = 0; i < neighborsMsg.Nodes.Count && _count < k; i++)
            {
                Node node = neighborsMsg.Nodes[i];
                if (node.HasDiscoveryEndpoint &&
                    CompositeDiscoveryApp.SupportsAddress(localIp, node.DiscoveryAddress.Address))
                {
                    _nodes[_count++] = node;
                }
            }

            isComplete = _count == k;
        }

        if (isComplete)
        {
            return TaskCompletionSource.TrySetResult(DiscoveryResponse<Node[]>.From(GetCurrentNodes()));
        }
        else
        {
            // Some client (nethermind, besu) only respond with one request.
            _ = CompleteAfterDelay();

            async Task CompleteAfterDelay()
            {
                if (Interlocked.Exchange(ref _timeoutInitiated, 1) != 0) return;
                await Task.Delay(_secondRequestTimeout);
                TaskCompletionSource.TrySetResult(DiscoveryResponse<Node[]>.From(GetCurrentNodes()));
            }
        }

        return !TaskCompletionSource.Task.IsCompleted;
    }

    private Node[] GetCurrentNodes()
    {
        lock (_lock)
        {
            if (_count == _nodes.Length)
            {
                return _nodes;
            }

            return _nodes.AsSpan(0, _count).ToArray();
        }
    }
}
