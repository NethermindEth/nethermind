// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record Discv5Nodes : Discv5Message
{
    public Discv5Nodes(ReadOnlySpan<byte> requestId, int total, IReadOnlyList<NodeRecord> records)
        : this(Discv5RequestId.From(requestId), total, records)
    {
    }

    public Discv5Nodes(Discv5RequestId requestId, int total, IReadOnlyList<NodeRecord> records, IDisposable? owner = null)
        : base(requestId, owner)
    {
        Total = total;
        Records = records;
    }

    public override Discv5MessageType MessageType => Discv5MessageType.Nodes;

    public int Total { get; }

    public IReadOnlyList<NodeRecord> Records { get; }
}
