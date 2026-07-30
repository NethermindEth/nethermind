// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>Protocol-level ENR acceptance policy of a discv5 instance.</summary>
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
