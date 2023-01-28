// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeAddResult
{
    public NodeAddResultType ResultType { get; private init; }

    public NodeBucketItem? EvictionCandidate { get; private init; }

    public static NodeAddResult Added()
    {
        return new NodeAddResult { ResultType = NodeAddResultType.Added };
    }

    public static NodeAddResult Full(NodeBucketItem evictionCandidate)
    {
        return new NodeAddResult { ResultType = NodeAddResultType.Full, EvictionCandidate = evictionCandidate };
    }
}
