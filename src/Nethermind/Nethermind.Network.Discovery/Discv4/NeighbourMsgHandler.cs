// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public class NeighbourMsgHandler(int k) : ITaskCompleter<Node[]>
{
    private Node[] _current = Array.Empty<Node>();
    public TaskCompletionSource<Node[]> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly TimeSpan _secondRequestTimeout = TimeSpan.FromSeconds(1);
    private bool _timeoutInitiated = false;

    public bool Handle(DiscoveryMsg msg)
    {
        NeighborsMsg neighborsMsg = (NeighborsMsg)msg;
        if (_current.Length >= k || _current.Length + neighborsMsg.Nodes.Length > k) return false;

        _current = _current.Concat(neighborsMsg.Nodes).ToArray();
        if (_current.Length == k)
        {
            TaskCompletionSource.TrySetResult(_current);
        }
        else
        {
            // Some client (nethermind) only respond with one request.
            Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref _timeoutInitiated, !_timeoutInitiated, false) == false) return;
                await Task.Delay(_secondRequestTimeout);
                TaskCompletionSource.TrySetResult(_current);
            });
        }
        return true;
    }
}
