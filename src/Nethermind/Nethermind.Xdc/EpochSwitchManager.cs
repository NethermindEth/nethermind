// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class EpochSwitchManager(
    ISpecProvider xdcSpecProvider,
    IBlockTree tree,
    ISnapshotManager snapshotManager)
    : BaseEpochSwitchManager(
        xdcSpecProvider,
        tree,
        snapshotManager)
{
    private LruCache<ulong, BlockRoundInfo> Round2EpochBlockInfo { get; } = new(XdcConstants.InMemoryRound2Epochs, nameof(Round2EpochBlockInfo));

    /// <summary>
    /// Determine if the given block is an epoch switch block.
    /// </summary>
    public override bool IsEpochSwitchAtBlock(XdcBlockHeader header)
    {
        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(header);

        if (header.Number < xdcSpec.SwitchBlock)
        {
            return header.Number % xdcSpec.EpochLength == 0;
        }

        if (header.Number == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (header.ExtraConsensusData is null)
        {
            return false;
        }

        ulong round = header.ExtraConsensusData.BlockRound;
        QuorumCertificate qc = header.ExtraConsensusData.QuorumCert;
        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - (round % xdcSpec.EpochLength);

        if (qc.ProposedBlockInfo.BlockNumber == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (parentRound < epochStartRound)
        {
            Round2EpochBlockInfo.Set(round, new BlockRoundInfo(header.Hash, round, header.Number));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determine if an epoch switch occurs at the given round, based on the parent block.
    /// </summary>
    public override bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent)
    {
        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(parent);

        if (parent.Number == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (parent.ExtraConsensusData is null)
        {
            return false;
        }

        ulong parentRound = parent.ExtraConsensusData.BlockRound;
        if (currentRound <= parentRound)
        {
            return false;
        }

        ulong epochStartRound = currentRound - (currentRound % xdcSpec.EpochLength);
        return parentRound < epochStartRound;
    }

    protected override ulong GetCurrentEpochNumber(EpochSwitchInfo epochSwitchInfo, IXdcReleaseSpec xdcSpec) =>
        xdcSpec.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / xdcSpec.EpochLength;

    protected override Address[] ResolvePenalties(XdcBlockHeader header, Snapshot _) =>
        header.PenaltiesAddress is null
            ? throw new InvalidOperationException($"PenaltiesAddress is null on epoch-switch block {header.Number}")
            : [.. header.PenaltiesAddress.Value];

    public override EpochSwitchInfo[]? GetEpochSwitchInfoBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        List<EpochSwitchInfo> epochSwitchInfos = [];

        Hash256 iteratorHash = end.Hash;
        ulong iteratorBlockNumber = end.Number;

        while (iteratorBlockNumber > start.Number)
        {
            EpochSwitchInfo epochSwitchInfo;

            if ((epochSwitchInfo = GetEpochSwitchInfo(iteratorHash)) is null)
            {
                return null;
            }

            if (epochSwitchInfo.EpochSwitchParentBlockInfo is null)
            {
                break;
            }

            iteratorHash = epochSwitchInfo.EpochSwitchParentBlockInfo.Hash;
            iteratorBlockNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;

            if (iteratorBlockNumber >= start.Number)
            {
                epochSwitchInfos.Add(epochSwitchInfo);
            }
        }

        epochSwitchInfos.Reverse();
        return epochSwitchInfos.ToArray();
    }

    private BlockRoundInfo? GetBlockInfoInCache(ulong estRound, ulong epoch)
    {
        List<BlockRoundInfo> epochSwitchInCache = [];

        for (ulong r = estRound; r < estRound + epoch; r++)
        {
            if (Round2EpochBlockInfo.TryGet(r, out BlockRoundInfo blockInfo))
            {
                epochSwitchInCache.Add(blockInfo);
            }
        }

        if (epochSwitchInCache.Count == 1)
        {
            return epochSwitchInCache[0];
        }

        if (epochSwitchInCache.Count == 0)
        {
            return null;
        }

        foreach (BlockRoundInfo blockInfo in epochSwitchInCache)
        {
            BlockHeader header = Tree.FindHeader(blockInfo.BlockNumber);
            if (header is null)
            {
                continue;
            }
            if (header.Hash == blockInfo.Hash)
            {
                return blockInfo;
            }
        }

        return null;
    }

    private bool TryBinarySearchBlockByEpochNumber(ulong targetEpochNumber, ulong start, ulong end, ulong switchBlock, ulong epoch, IXdcReleaseSpec xdcSpec, out BlockRoundInfo epochBlockInfo)
    {
        while (start < end)
        {
            // Use start + (end - start) / 2 instead of (start + end) / 2 to avoid
            // ulong overflow when both start and end are large block numbers.
            ulong mid = start + (end - start) / 2;
            XdcBlockHeader? header = (XdcBlockHeader?)Tree.FindHeader(mid);
            if (header is null)
            {
                epochBlockInfo = null;
                return false;
            }

            if (header.ExtraConsensusData is null)
            {
                epochBlockInfo = null;
                return false;
            }

            bool isEpochSwitch = IsEpochSwitchAtBlock(header);
            ulong epochNum = xdcSpec.SwitchEpoch + (header.ExtraConsensusData?.BlockRound ?? 0) / xdcSpec.EpochLength;

            if (epochNum == targetEpochNumber)
            {
                ulong round = header.ExtraConsensusData.BlockRound;

                if (isEpochSwitch)
                {
                    epochBlockInfo = new BlockRoundInfo(header.Hash, round, header.Number);
                    return true;
                }
                else
                {
                    end = header.Number;
                    // Shorten the search range by stepping back at most (round % epoch) blocks.
                    ulong roundOffset = round % epoch;
                    start = end >= roundOffset ? Math.Max(start, end - roundOffset) : start;
                }
            }
            else if (epochNum > targetEpochNumber)
            {
                end = header.Number;
            }
            else
            {
                ulong nextStart = header.Number;
                if (nextStart == start)
                {
                    break;
                }
                start = nextStart;
            }
        }

        epochBlockInfo = null;
        return false;
    }

    public override BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch)
    {
        XdcBlockHeader? headHeader = (XdcBlockHeader?)Tree.Head?.Header;
        if (headHeader is null)
        {
            return null;
        }
        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(headHeader);

        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headHeader);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        ulong epochNumber = xdcSpec.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / xdcSpec.EpochLength;

        if (targetEpoch == epochNumber)
        {
            return epochSwitchInfo.EpochSwitchBlockInfo;
        }

        if (targetEpoch > epochNumber)
        {
            return null;
        }

        if (targetEpoch < xdcSpec.SwitchEpoch)
        {
            return null;
        }

        ulong estRound = (targetEpoch - xdcSpec.SwitchEpoch) * xdcSpec.EpochLength;

        BlockRoundInfo epochBlockInfo = GetBlockInfoInCache(estRound, xdcSpec.EpochLength);
        if (epochBlockInfo is not null)
        {
            return epochBlockInfo;
        }

        ulong epoch = xdcSpec.EpochLength;
        ulong estBlockNumDiff = epoch * (epochNumber - targetEpoch);
        ulong estBlockNum = Math.Max(
            xdcSpec.SwitchBlock,
            epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber.SaturatingSub(estBlockNumDiff));

        ulong closeEpochNum = 2ul;

        if (closeEpochNum >= epochNumber - targetEpoch)
        {
            XdcBlockHeader? estBlockHeader = (XdcBlockHeader?)Tree.FindHeader(estBlockNum);
            if (estBlockHeader is null)
            {
                return null;
            }
            EpochSwitchInfo[] epochSwitchInfos = GetEpochSwitchInfoBetween(estBlockHeader, headHeader);
            if (epochSwitchInfos is null)
            {
                return null;
            }
            foreach (EpochSwitchInfo info in epochSwitchInfos)
            {
                ulong epochNum = xdcSpec.SwitchEpoch + info.EpochSwitchBlockInfo.Round / xdcSpec.EpochLength;
                if (epochNum == targetEpoch)
                {
                    return info.EpochSwitchBlockInfo;
                }
            }
        }

        if (!TryBinarySearchBlockByEpochNumber(targetEpoch, estBlockNum, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber, xdcSpec.SwitchBlock, xdcSpec.EpochLength, xdcSpec, out epochBlockInfo))
        {
            return null;
        }

        return epochBlockInfo;
    }
}
