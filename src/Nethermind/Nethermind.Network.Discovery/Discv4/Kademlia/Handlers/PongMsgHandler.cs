// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

/// <summary>Completes a pending ping with the pong that echoes it from the pinged IP.</summary>
/// <remarks>A pong from any other IP proves nothing about the pinged node and is left unmatched,
/// so it can neither bond that endpoint nor preempt the genuine reply. The port may differ when a NAT
/// remaps it; the caller decides whether such a pong bonds, and endpoint proofs that need the exact
/// port reject it before it reaches this handler.</remarks>
public sealed class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
{
    public TaskCompletionSource<DiscoveryResponse<PongMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is PongMsg pong
        && ping.Mdc is { } expected
        && pong.PingMdc == expected
        && ping.FarAddress is { } destination
        && pong.FarAddress is { } source
        && source.Address.NormalizeMappedIPv4().Equals(destination.Address.NormalizeMappedIPv4())
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<PongMsg>.From(pong));
}
