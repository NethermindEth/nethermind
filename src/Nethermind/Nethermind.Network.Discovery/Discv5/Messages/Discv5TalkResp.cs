// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5TalkResp : Discv5Message
{
    public Discv5TalkResp(ReadOnlySpan<byte> requestId, ReadOnlyMemory<byte> response)
        : this(Discv5RequestId.From(requestId), response)
    {
    }

    public Discv5TalkResp(Discv5RequestId requestId, ReadOnlyMemory<byte> response, IDisposable? owner = null)
        : base(requestId, owner)
        => Response = response;

    public override Discv5MessageType MessageType => Discv5MessageType.TalkResp;

    public ReadOnlyMemory<byte> Response { get; }
}
