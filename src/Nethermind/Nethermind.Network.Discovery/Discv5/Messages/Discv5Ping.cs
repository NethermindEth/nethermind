// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5Ping : Discv5Message
{
    public Discv5Ping(ReadOnlySpan<byte> requestId, ulong enrSequence)
        : this(Discv5RequestId.From(requestId), enrSequence)
    {
    }

    public Discv5Ping(Discv5RequestId requestId, ulong enrSequence, IDisposable? owner = null)
        : base(requestId, owner)
        => EnrSequence = enrSequence;

    public override Discv5MessageType MessageType => Discv5MessageType.Ping;

    public ulong EnrSequence { get; }
}
