// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SubnetEpochSwitchManager : BaseEpochSwitchManager
{
    public SubnetEpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager)
        : base(xdcSpecProvider, tree, snapshotManager)
    {
    }

    public override bool IsEpochSwitchAtBlock(XdcBlockHeader header)
    {
        IXdcReleaseSpec xdcSpec = _xdcSpecProvider.GetXdcSpec(header);
        return header.Number % xdcSpec.EpochLength == 0;
    }

    public override bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent)
    {
        IXdcReleaseSpec xdcSpec = _xdcSpecProvider.GetXdcSpec(parent);
        return (parent.Number + 1) % xdcSpec.EpochLength == 0;
    }

    protected override Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec)
    {
        if (snapshot is not SubnetSnapshot subnetSnapshot)
        {
            throw new ArgumentException("Snapshot is not a SubnetSnapshot", nameof(snapshot));
        }

        return subnetSnapshot.NextEpochPenalties;
    }

    public override EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert)
    {
        // https://github.com/XinFinOrg/XDC-Subnet/blob/master/consensus/XDPoS/engines/engine_v2/timeout.go
        var xdcHeader = (XdcBlockHeader)_tree.Head?.Header;
        if (xdcHeader is null)
        {
            return null;
        }
        return GetEpochSwitchInfo(xdcHeader);
    }

    public override BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch)
    {

        throw new NotImplementedException();
    }
}
