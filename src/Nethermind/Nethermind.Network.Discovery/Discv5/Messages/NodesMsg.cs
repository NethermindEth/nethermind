// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed record NodesMsg : Discv5Message
{
    public NodesMsg(ReadOnlySpan<byte> requestId, int total, IReadOnlyList<NodeRecord> records)
        : this(RequestId.From(requestId), total, records)
    {
    }

    public NodesMsg(in RequestId requestId, int total, IReadOnlyList<NodeRecord> records, ArrayPoolSpan<byte>? owner = null)
        : base(in requestId, owner)
    {
        Total = total;
        Records = records;
    }

    public override MessageType MessageType => MessageType.Nodes;

    public int Total { get; }

    public IReadOnlyList<NodeRecord> Records { get; }
}
