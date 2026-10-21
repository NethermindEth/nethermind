// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record PongMsg : Discv5Message
{
    public PongMsg(ReadOnlySpan<byte> requestId, ulong enrSequence, IPAddress recipientIp, int recipientPort)
        : this(RequestId.From(requestId), enrSequence, recipientIp, recipientPort)
    {
    }

    public PongMsg(in RequestId requestId, ulong enrSequence, IPAddress recipientIp, int recipientPort, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
    {
        EnrSequence = enrSequence;
        RecipientIp = recipientIp;
        RecipientPort = recipientPort;
    }

    public override MessageType MessageType => MessageType.Pong;

    public ulong EnrSequence { get; }

    public IPAddress RecipientIp { get; }

    public int RecipientPort { get; }
}
