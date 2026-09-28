// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record TalkRespMsg : Discv5Message
{
    public TalkRespMsg(ReadOnlySpan<byte> requestId, ReadOnlyMemory<byte> response)
        : this(RequestId.From(requestId), response)
    {
    }

    public TalkRespMsg(in RequestId requestId, ReadOnlyMemory<byte> response, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
        => _response = response;

    public override MessageType MessageType => MessageType.TalkResp;

    private readonly ReadOnlyMemory<byte> _response;

    internal ReadOnlySpan<byte> Response => _response.Span;
}
