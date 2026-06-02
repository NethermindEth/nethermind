// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

public class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
{
    public TaskCompletionSource<DiscoveryResponse<PongMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is PongMsg pong
        && Bytes.AreEqual(pong.PingMdc, ping.Mdc)
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<PongMsg>.From(pong));
}
