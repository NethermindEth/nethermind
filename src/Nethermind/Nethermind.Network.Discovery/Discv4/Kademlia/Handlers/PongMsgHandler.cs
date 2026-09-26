// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

public sealed class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
{
    public TaskCompletionSource<DiscoveryResponse<PongMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is PongMsg pong
        && ping.Mdc is { } expected
        && pong.PingMdc == expected
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<PongMsg>.From(pong));
}
