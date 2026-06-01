// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Handlers;

internal sealed class PongResponseHandler(Node receiver) : ResponseHandler<Discv5Pong>(Discv5MessageType.Pong)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task Task => _completion.Task;

    public override bool Handle(Discv5Pong message)
    {
        receiver.ValidatedProtocol = true;
        _completion.TrySetResult();
        return true;
    }
}
