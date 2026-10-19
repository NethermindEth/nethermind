// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

/// <summary>Completes a pending ping with the pong that echoes it from the pinged endpoint.</summary>
/// <remarks>A pong from any other endpoint proves nothing about the pinged one and is left unmatched,
/// so it can neither bond that endpoint nor preempt the genuine reply.</remarks>
public sealed class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
{
    public TaskCompletionSource<DiscoveryResponse<PongMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is PongMsg pong
        && ping.Mdc is { } expected
        && pong.PingMdc == expected
        && ping.FarAddress is { } pinged
        && pinged.Equals(pong.FarAddress)
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<PongMsg>.From(pong));
}
