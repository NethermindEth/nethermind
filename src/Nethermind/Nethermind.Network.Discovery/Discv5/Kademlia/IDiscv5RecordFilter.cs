// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>ENR acceptance policy applied at a discv5 integration boundary.</summary>
public interface IDiscv5RecordFilter
{
    /// <summary>Returns whether the integration boundary must exclude the record.</summary>
    bool Excludes(NodeRecord record);
}

/// <summary>Drops consensus-only ENRs from execution peer discovery because beacon nodes cannot be dialed over RLPx.</summary>
public sealed class ExecutionLayerDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static ExecutionLayerDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => DiscoveryV5App.IsConsensusOnlyNodeRecord(record);
}

/// <summary>Accepts every record.</summary>
public sealed class AcceptAllDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static AcceptAllDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => false;
}
