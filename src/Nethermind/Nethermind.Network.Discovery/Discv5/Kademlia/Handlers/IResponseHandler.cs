// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Messages;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal interface IResponseHandler
{
    Task Task { get; }

    MessageType MessageType { get; }

    bool Handle(Discv5Message message);
}

internal interface IResponseHandler<in TMessage> : IResponseHandler where TMessage : Discv5Message
{
    bool Handle(TMessage message);
}

internal abstract class ResponseHandler<TMessage>(MessageType messageType) : IResponseHandler<TMessage>
    where TMessage : Discv5Message
{
    public abstract Task Task { get; }

    public MessageType MessageType { get; } = messageType;

    public bool Handle(Discv5Message message) => message is TMessage typedMessage && Handle(typedMessage);

    public abstract bool Handle(TMessage message);
}
