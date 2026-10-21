// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    protected override ulong GetCurrentEpochNumber(EpochSwitchInfo epochSwitchInfo, IXdcReleaseSpec xdcSpec) =>
        epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber / xdcSpec.EpochLength;

    protected override Address[] ResolvePenalties(XdcBlockHeader _, Snapshot snapshot)
    {
        if (snapshot is not SubnetSnapshot subnetSnapshot)
            throw new ArgumentException("Snapshot is not a SubnetSnapshot", nameof(snapshot));

        return subnetSnapshot.NextEpochPenalties;
    }

    public override BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch)
    {
        XdcBlockHeader? headHeader = (XdcBlockHeader?)Tree.Head?.Header;
        if (headHeader is null) return null;

        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(headHeader);

        if (targetEpoch > long.MaxValue / xdcSpec.EpochLength) return null;
        ulong targetNumber = targetEpoch * xdcSpec.EpochLength;

        XdcBlockHeader? targetHeader = (XdcBlockHeader?)Tree.FindHeader(targetNumber);
        if (targetHeader is null) return null;

        return FindEpochSwitchHeader(targetHeader) is { } epochSwitchHeader ? ToBlockRoundInfo(epochSwitchHeader) : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Subnet epoch switches sit on exact multiples of the epoch length, so the blocks in range are computed directly
    /// instead of walking the chain as <see cref="EpochSwitchManager"/> does. Resolution is therefore canonical by
    /// block number: the branch <paramref name="start"/> and <paramref name="end"/> sit on is not honoured, so a
    /// non-canonical header yields canonical results rather than that branch's ancestors.
    /// </remarks>
    public override EpochSwitchInfo[]? GetEpochSwitchInfoBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        if (end.Number <= start.Number) return [];

        ulong epochLength = XdcSpecProvider.GetXdcSpec(end).EpochLength;
        ulong offset = start.Number % epochLength;
        ulong first = offset == 0 ? start.Number : start.Number + (epochLength - offset);
        ulong last = end.Number - end.Number % epochLength;
        if (first > last) return [];

        List<EpochSwitchInfo> epochSwitchInfos = [];
        for (ulong number = first; number <= last; number += epochLength)
        {
            if (Tree.FindHeader(number) is not XdcBlockHeader header) return null;

            EpochSwitchInfo? epochSwitchInfo = GetEpochSwitchInfo(header);
            if (epochSwitchInfo is null) return null;

            // The switch block carries no quorum certificate, so it has no parent block info. EpochSwitchManager
            // stops its backward walk there rather than reporting it, and callers expect the same set.
            if (epochSwitchInfo.EpochSwitchParentBlockInfo is null) continue;

            epochSwitchInfos.Add(epochSwitchInfo);
        }

        return epochSwitchInfos.ToArray();
    }
}
