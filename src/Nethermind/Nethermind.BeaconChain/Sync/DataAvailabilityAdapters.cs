// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// Serves the importer's availability rule from the gossip-validated sidecar pool. Nothing else
/// fills that pool today, so a block whose columns never arrived over gossip (every range-synced
/// block) has no columns here and is judged unavailable.
/// </summary>
internal sealed class DataColumnPoolSource(DataColumnSidecarPool pool) : IDataColumnSource
{
    public bool TryGetColumn(Hash256 blockRoot, ulong columnIndex, [NotNullWhen(true)] out DataColumnSidecar? sidecar) =>
        pool.TryGet(blockRoot, columnIndex, out sidecar);
}

/// <summary>
/// This node's column custody, read from discovery once it has started. The importer is built before
/// discovery starts, so the identity cannot be captured up front; it is resolved at check time from
/// the custody discovery currently advertises and re-derived only when that custody's node id or
/// group count changes. With no discovery at all (the P2P-less configuration tests run) there is no
/// identity, and the rule fed from here fails closed.
/// </summary>
internal sealed class DiscoveryNodeCustodySource(BeaconDiscovery? discovery) : INodeColumnCustodySource
{
    private NodeColumnCustody? _current;

    public NodeColumnCustody? Current
    {
        get
        {
            // Assigned inside Start; a null custody means discovery has not run yet.
            if (discovery?.LocalCustody is not { } custody)
            {
                return null;
            }

            NodeColumnCustody? current = _current;
            if (current is null || current.NodeId != custody.NodeId || current.CustodyGroupCount != custody.CustodyGroupCount)
            {
                _current = current = new NodeColumnCustody(custody.NodeId, custody.CustodyGroupCount);
            }

            return current;
        }
    }
}
