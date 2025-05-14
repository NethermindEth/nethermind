// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Discv4;

public class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
{
    public TaskCompletionSource<PongMsg> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Handle(DiscoveryMsg msg)
    {
        if (msg is PongMsg pong && Bytes.AreEqual(pong.PingMdc, ping.Mdc) && TaskCompletionSource.TrySetResult(pong))
        {
            return true;
        }
        return false;
    }
}
