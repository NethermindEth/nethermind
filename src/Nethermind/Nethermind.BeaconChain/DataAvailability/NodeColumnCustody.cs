// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The columns one node identity is responsible for under EIP-7594: the columns it custodies and
/// serves long-term, and the (superset) columns it must download and check every slot before it
/// may treat a blob-carrying block as available (das-core.md "custody sampling").
/// </summary>
/// <remarks>
/// Both sets are derived from <see cref="CustodyGroups"/> and <see cref="DataAvailabilitySampling"/>
/// alone, from the same node id the P2P layer advertises, so what this node demands of a block and
/// what it tells peers it custodies cannot drift apart.
/// </remarks>
public sealed class NodeColumnCustody
{
    /// <param name="nodeId">The node's discv5 node id, as <see cref="CustodyGroups.GetCustodyGroups"/> expects it.</param>
    /// <param name="custodyGroupCount">The custody group count this node advertises (<c>cgc</c>).</param>
    public NodeColumnCustody(Hash256 nodeId, ulong custodyGroupCount)
    {
        NodeId = nodeId;
        CustodyGroupCount = custodyGroupCount;

        SortedSet<ulong> custody = [];
        foreach (ulong group in CustodyGroups.GetCustodyGroups(nodeId, custodyGroupCount))
        {
            foreach (ulong column in CustodyGroups.ComputeColumnsForCustodyGroup(group))
            {
                custody.Add(column);
            }
        }

        CustodyColumns = [.. custody];
        SampledColumns = DataAvailabilitySampling.GetColumnsToSample(nodeId, custodyGroupCount);
    }

    public Hash256 NodeId { get; }

    public ulong CustodyGroupCount { get; }

    /// <summary>Distinct, sorted columns this node custodies: <c>compute_columns_for_custody_group</c> over <c>get_custody_groups(node_id, custody_group_count)</c>.</summary>
    public IReadOnlyList<ulong> CustodyColumns { get; }

    /// <summary>Distinct, sorted columns this node samples every slot; always contains <see cref="CustodyColumns"/>.</summary>
    public IReadOnlyList<ulong> SampledColumns { get; }
}

/// <summary>
/// Resolves this node's <see cref="NodeColumnCustody"/> on demand. The node id is only known once
/// discovery has started, which is after the block importer is built, so the identity is read at
/// check time rather than captured at construction; <c>null</c> means it is not known yet.
/// </summary>
public interface INodeColumnCustodySource
{
    NodeColumnCustody? Current { get; }
}
