// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public class NeighbourMsgHandler(int k) : ITaskCompleter<Node[]>
{
    private Node[] _current = [];
    public TaskCompletionSource<DiscoveryResponse<Node[]>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The peer should send the two packet pretty much immediately. In any case, if the second packet is loss, its not a huge deal.
    private static readonly TimeSpan _secondRequestTimeout = TimeSpan.FromMilliseconds(100);
    private bool _timeoutInitiated = false;

    public bool Handle(DiscoveryMsg msg)
    {
        if (TaskCompletionSource.Task.IsCompleted) return false;

        NeighborsMsg neighborsMsg = (NeighborsMsg)msg;

        while (true)
        {
            if (TaskCompletionSource.Task.IsCompleted) return false;

            Node[] current = _current;
            if (current.Length >= k || current.Length + neighborsMsg.Nodes.Count > k) return false;
            if (Interlocked.CompareExchange(ref _current, [.. current, .. neighborsMsg.Nodes], current) == current) break;
        }

        if (_current.Length == k)
        {
            return TaskCompletionSource.TrySetResult(DiscoveryResponse<Node[]>.From(_current));
        }
        else
        {
            // Some client (nethermind, besu) only respond with one request.
            _ = CompleteAfterDelay();

            async Task CompleteAfterDelay()
            {
                if (Interlocked.CompareExchange(ref _timeoutInitiated, true, false)) return;
                await Task.Delay(_secondRequestTimeout);
                TaskCompletionSource.TrySetResult(DiscoveryResponse<Node[]>.From(_current));
            }
        }

        return !TaskCompletionSource.Task.IsCompleted;
    }
}
