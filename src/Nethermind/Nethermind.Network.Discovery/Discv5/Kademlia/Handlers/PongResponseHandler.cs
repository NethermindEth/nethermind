// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class PongResponseHandler(Node receiver) : ResponseHandler<PongMsg>(MessageType.Pong)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task Task => _completion.Task;

    public ulong EnrSequence { get; private set; }

    public override bool Handle(PongMsg message)
    {
        EnrSequence = message.EnrSequence;
        receiver.ValidatedProtocol = true;
        _completion.TrySetResult();
        return true;
    }
}
