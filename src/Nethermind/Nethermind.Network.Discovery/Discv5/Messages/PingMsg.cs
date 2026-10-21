// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record PingMsg : Discv5Message
{
    public PingMsg(ReadOnlySpan<byte> requestId, ulong enrSequence)
        : this(RequestId.From(requestId), enrSequence)
    {
    }

    public PingMsg(in RequestId requestId, ulong enrSequence, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
        => EnrSequence = enrSequence;

    public override MessageType MessageType => MessageType.Ping;

    public ulong EnrSequence { get; }
}
