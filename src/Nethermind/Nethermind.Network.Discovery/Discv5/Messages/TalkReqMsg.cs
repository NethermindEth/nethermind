// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record TalkReqMsg : Discv5Message
{
    public TalkReqMsg(ReadOnlySpan<byte> requestId, ReadOnlyMemory<byte> protocol, ReadOnlyMemory<byte> request)
        : this(RequestId.From(requestId), protocol, request)
    {
    }

    public TalkReqMsg(in RequestId requestId, ReadOnlyMemory<byte> protocol, ReadOnlyMemory<byte> request, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
    {
        _protocol = protocol;
        _request = request;
    }

    public override MessageType MessageType => MessageType.TalkReq;

    private readonly ReadOnlyMemory<byte> _protocol;

    private readonly ReadOnlyMemory<byte> _request;

    internal ReadOnlySpan<byte> Protocol => _protocol.Span;

    internal ReadOnlySpan<byte> Request => _request.Span;
}
