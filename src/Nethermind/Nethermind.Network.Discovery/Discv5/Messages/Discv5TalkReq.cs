// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5TalkReq : Discv5Message
{
    public Discv5TalkReq(ReadOnlySpan<byte> requestId, ReadOnlyMemory<byte> protocol, ReadOnlyMemory<byte> request)
        : this(Discv5RequestId.From(requestId), protocol, request)
    {
    }

    public Discv5TalkReq(Discv5RequestId requestId, ReadOnlyMemory<byte> protocol, ReadOnlyMemory<byte> request, IDisposable? owner = null)
        : base(requestId, owner)
    {
        Protocol = protocol;
        Request = request;
    }

    public override Discv5MessageType MessageType => Discv5MessageType.TalkReq;

    public ReadOnlyMemory<byte> Protocol { get; }

    public ReadOnlyMemory<byte> Request { get; }
}
