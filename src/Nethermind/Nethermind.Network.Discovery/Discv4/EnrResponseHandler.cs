// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Discv4;

public class EnrResponseHandler : ITaskCompleter<EnrResponseMsg>
{
    public TaskCompletionSource<EnrResponseMsg> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg)
    {
        if (msg is EnrResponseMsg resp && TaskCompletionSource.TrySetResult(resp))
        {
            return true;
        }
        return false;
    }
}
