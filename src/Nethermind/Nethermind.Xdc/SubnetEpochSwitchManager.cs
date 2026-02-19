// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SubnetEpochSwitchManager : EpochSwitchManager
{
    public SubnetEpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager)
        : base(xdcSpecProvider, tree, snapshotManager)
    {
    }

    protected override Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec)
    {
        // Get penalties from snapshot (for subnet version)
        if (snapshot is SubnetSnapshot subnetSnapshot)
        {
            return subnetSnapshot.NextEpochPenalties;
        }

        // Fallback to empty array if snapshot is not SubnetSnapshot
        return [];
    }
}
