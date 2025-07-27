// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public class NeighbourMsgHandler(int k) : ITaskCompleter<Node[]>
{
    private Node[] _current = Array.Empty<Node>();
    public TaskCompletionSource<Node[]> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The peer should send the two packet pretty much immediately. In any case, if the second packet is loss, its not a huge deal.
    private static readonly TimeSpan _secondRequestTimeout = TimeSpan.FromMilliseconds(100);
    private bool _timeoutInitiated = false;

    public bool Handle(DiscoveryMsg msg)
    {
        NeighborsMsg neighborsMsg = (NeighborsMsg)msg;

        while (true)
        {
            Node[] current = _current;
            if (current.Length >= k || current.Length + neighborsMsg.Nodes.Count > k) return false;
            if (Interlocked.CompareExchange(ref _current, _current.Concat(neighborsMsg.Nodes).ToArray(), current) == current) break;
        }

        if (_current.Length == k)
        {
            TaskCompletionSource.TrySetResult(_current);
        }
        else
        {
            // Some client (nethermind, besu) only respond with one request.
            Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref _timeoutInitiated, !_timeoutInitiated, false)) return;
                await Task.Delay(_secondRequestTimeout);
                TaskCompletionSource.TrySetResult(_current);
            });
        }
        return true;
    }
}
