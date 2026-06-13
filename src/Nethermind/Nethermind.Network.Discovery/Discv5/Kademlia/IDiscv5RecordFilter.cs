// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>Protocol-level ENR acceptance policy of a discv5 instance.</summary>
/// <remarks>
/// Discv5 is one DHT shared by execution- and consensus-layer nodes, so every instance sees both
/// kinds of records. Which records are useful is a property of the instance's purpose, not of the
/// protocol: the execution layer's instance discovers RLPx peers and must drop consensus-only ENRs
/// (an <c>eth2</c> entry without an <c>eth</c> entry) or its routing table fills with peers it can
/// never dial; a consensus-layer instance — e.g. the embedded beacon chain driver's discovery —
/// exists precisely to find those records. The policy is therefore a required dependency of the
/// record-handling components (<see cref="Handlers.NodesResponseHandler"/>,
/// <see cref="KademliaAdapter"/>, <see cref="NodeSource"/>) rather than a hard-coded check;
/// <see cref="KademliaModule"/> registers <see cref="ExecutionLayerDiscv5RecordFilter"/> by
/// default, and a consensus-layer composition scope overrides the registration with
/// <see cref="AcceptAllDiscv5RecordFilter"/>.
/// </remarks>
public interface IDiscv5RecordFilter
{
    /// <summary>Returns whether this discv5 instance must drop the record.</summary>
    bool Excludes(NodeRecord record);
}

/// <summary>Drops consensus-only ENRs: the execution layer cannot dial beacon nodes over RLPx.</summary>
public sealed class ExecutionLayerDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static ExecutionLayerDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => DiscoveryV5App.IsConsensusOnlyNodeRecord(record);
}

/// <summary>Accepts every record; used by consensus-layer discovery, which filters by fork digest downstream.</summary>
public sealed class AcceptAllDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static AcceptAllDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => false;
}
