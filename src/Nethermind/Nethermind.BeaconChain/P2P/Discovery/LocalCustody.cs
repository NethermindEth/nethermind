// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>
/// This node's own custody groups and gossip subnets, derived once from its stable discv5 node id.
/// </summary>
/// <remarks>
/// Every value here is computed from <see cref="DataAvailability.CustodyGroups"/> alone (no
/// independent constant), so the ENR <c>cgc</c> entry, the subscribed
/// <c>data_column_sidecar_{subnet_id}</c> topics and this node's own advertised
/// <see cref="Types.MetaDataV3.CustodyGroupCount"/> can never drift apart.
/// </remarks>
public sealed class LocalCustody
{
    /// <summary>The discv5 node id every value here was derived from, exactly as it was supplied.</summary>
    public Hash256 NodeId { get; }

    /// <summary>The number of custody groups this node is responsible for.</summary>
    public ulong CustodyGroupCount { get; }

    /// <summary>The distinct, sorted custody groups this node is responsible for.</summary>
    public IReadOnlyList<ulong> CustodyGroups { get; }

    /// <summary>The distinct, sorted gossip subnets (<c>data_column_sidecar_{subnet_id}</c>) carrying this node's custodied columns.</summary>
    public IReadOnlyList<ulong> Subnets { get; }

    /// <param name="nodeId">
    /// The node's raw discv5 node id (<c>keccak256(public_key)</c>), passed through byte-for-byte;
    /// <see cref="DataAvailability.CustodyGroups.GetCustodyGroups"/> performs the big-endian to
    /// little-endian reordering the spec's <c>NodeID</c> integer requires.
    /// </param>
    public LocalCustody(Hash256 nodeId, ulong custodyGroupCount)
    {
        NodeId = nodeId;
        CustodyGroupCount = custodyGroupCount;
        ulong[] groups = DataAvailability.CustodyGroups.GetCustodyGroups(nodeId, custodyGroupCount);
        CustodyGroups = groups;

        SortedSet<ulong> subnets = [];
        foreach (ulong group in groups)
        {
            foreach (ulong column in DataAvailability.CustodyGroups.ComputeColumnsForCustodyGroup(group))
            {
                subnets.Add(DataAvailability.CustodyGroups.ComputeSubnetForDataColumnSidecar(column));
            }
        }

        Subnets = [.. subnets];
    }
}
