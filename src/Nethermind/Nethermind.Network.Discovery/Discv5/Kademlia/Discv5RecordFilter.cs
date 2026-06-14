// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>Protocol-level ENR acceptance policy of a discv5 instance.</summary>
/// <remarks>
/// Execution-layer discovery rejects consensus-only ENRs (an <c>eth2</c> entry without an <c>eth</c> entry)
/// since those nodes are not RLPx peers. A consensus-layer discv5 instance (for example the embedded beacon
/// chain driver) overrides the <see cref="ExecutionLayer"/> registration with <see cref="AcceptAll"/> to
/// discover those very nodes.
/// </remarks>
public sealed class Discv5RecordFilter(bool acceptConsensusOnlyRecords)
{
    public static Discv5RecordFilter ExecutionLayer { get; } = new(acceptConsensusOnlyRecords: false);

    public static Discv5RecordFilter AcceptAll { get; } = new(acceptConsensusOnlyRecords: true);

    /// <summary>Returns whether this discv5 instance must drop the record.</summary>
    public bool Excludes(NodeRecord record) => !acceptConsensusOnlyRecords && DiscoveryV5App.IsConsensusOnlyNodeRecord(record);
}
