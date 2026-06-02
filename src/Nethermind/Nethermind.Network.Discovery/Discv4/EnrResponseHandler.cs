// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Discv4;

public class EnrResponseHandler(EnrRequestMsg request) : ITaskCompleter<EnrResponseMsg>
{
    public TaskCompletionSource<DiscoveryResponse<EnrResponseMsg>> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg) =>
        !TaskCompletionSource.Task.IsCompleted
        && msg is EnrResponseMsg resp
        && request.Hash is { } expected
        && Bytes.AreEqual(resp.RequestKeccak.Bytes, expected.Span)
        && TaskCompletionSource.TrySetResult(DiscoveryResponse<EnrResponseMsg>.From(resp));
}
