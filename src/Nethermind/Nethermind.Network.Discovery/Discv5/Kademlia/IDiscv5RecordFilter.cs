// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>Determines whether a discv5 ENR can be exposed as an execution-layer peer candidate.</summary>
public interface IDiscv5RecordFilter
{
    /// <summary>Returns whether the record must be excluded from execution-layer peer discovery.</summary>
    bool Excludes(NodeRecord record);
}

/// <summary>Excludes consensus-only ENRs because beacon nodes cannot be dialed over RLPx.</summary>
public sealed class ExecutionLayerDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static ExecutionLayerDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => DiscoveryV5App.IsConsensusOnlyNodeRecord(record);
}
