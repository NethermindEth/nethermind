// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5FindNode : Discv5Message
{
    public Discv5FindNode(ReadOnlySpan<byte> requestId, ReadOnlySpan<int> distances)
        : this(Discv5RequestId.From(requestId), new Discv5Distances(distances))
    {
    }

    public Discv5FindNode(Discv5RequestId requestId, Discv5Distances distances, IDisposable? owner = null)
        : base(requestId, owner)
        => Distances = distances;

    public override Discv5MessageType MessageType => Discv5MessageType.FindNode;

    public Discv5Distances Distances { get; }

    protected override void DisposeCore() => Distances.Dispose();
}
