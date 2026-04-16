// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SubnetEpochSwitchManager(
    ISpecProvider xdcSpecProvider,
    IBlockTree tree,
    ISnapshotManager snapshotManager)
    : BaseEpochSwitchManager(xdcSpecProvider, tree, snapshotManager)
{
    // Subnet epoch switches are block-number-based, not round-based
    public override bool IsEpochSwitchAtBlock(XdcBlockHeader header)
    {
        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(header);
        return header.Number % xdcSpec.EpochLength == 0;
    }

    public override bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent)
    {
        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(parent);
        return (parent.Number + 1) % xdcSpec.EpochLength == 0;
    }

    protected override Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec)
    {
        if (snapshot is not SubnetSnapshot subnetSnapshot)
            throw new ArgumentException("Snapshot is not a SubnetSnapshot", nameof(snapshot));

        return subnetSnapshot.NextEpochPenalties;
    }

    public override BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch) =>
        throw new NotSupportedException("GetBlockByEpochNumber is not supported in subnet.");
}
