// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

public sealed class EnrResponseHandler(EnrRequestMsg request, IPEndPoint expectedEndpoint) : ITaskCompleter<EnrResponseMsg>
{
    public TaskCompletionSource<DiscoveryResponse<EnrResponseMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is EnrResponseMsg resp
        && request.Hash is { } expected
        && resp.RequestKeccak == expected
        && expectedEndpoint.Equals(resp.FarAddress)
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<EnrResponseMsg>.From(resp));
}
