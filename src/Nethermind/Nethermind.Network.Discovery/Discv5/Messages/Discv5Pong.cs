// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5Pong : Discv5Message
{
    public Discv5Pong(ReadOnlySpan<byte> requestId, ulong enrSequence, IPAddress recipientIp, int recipientPort)
        : this(Discv5RequestId.From(requestId), enrSequence, recipientIp, recipientPort)
    {
    }

    public Discv5Pong(Discv5RequestId requestId, ulong enrSequence, IPAddress recipientIp, int recipientPort, IDisposable? owner = null)
        : base(requestId, owner)
    {
        EnrSequence = enrSequence;
        RecipientIp = recipientIp;
        RecipientPort = recipientPort;
    }

    public override Discv5MessageType MessageType => Discv5MessageType.Pong;

    public ulong EnrSequence { get; }

    public IPAddress RecipientIp { get; }

    public int RecipientPort { get; }
}
