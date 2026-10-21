// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record FindNodeMsg : Discv5Message
{
    public FindNodeMsg(ReadOnlySpan<byte> requestId, ReadOnlySpan<int> distances)
        : this(RequestId.From(requestId), new Distances(distances))
    {
    }

    public FindNodeMsg(in RequestId requestId, Distances distances, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
        => Distances = distances;

    public override MessageType MessageType => MessageType.FindNode;

    public Distances Distances { get; }

    protected override void DisposeCore() => Distances.Dispose();
}
