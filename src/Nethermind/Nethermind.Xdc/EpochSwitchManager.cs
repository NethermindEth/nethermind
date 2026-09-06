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

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves the epoch switch blocks first and reads their master node sets afterwards, so an epoch that the walk
    /// only passes through doesn't need to have its snapshot available.
    /// </remarks>
    public override EpochSwitchInfo[]? GetEpochSwitchInfoBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        if (GetEpochSwitchBlocksBetween(start, end) is not { } epochSwitchBlocks)
        {
            return null;
        }

        EpochSwitchInfo[] epochSwitchInfos = new EpochSwitchInfo[epochSwitchBlocks.Length];

        for (int i = 0; i < epochSwitchBlocks.Length; i++)
        {
            if (GetEpochSwitchInfo(epochSwitchBlocks[i].Hash) is not { } epochSwitchInfo)
            {
                return null;
            }

            epochSwitchInfos[i] = epochSwitchInfo;
        }

        return epochSwitchInfos;
    }

    /// <summary>
    /// Collects the epoch switch blocks between <paramref name="start"/> and <paramref name="end"/>, oldest first.
    /// </summary>
    /// <remarks>
    /// Needs no snapshot, so it navigates epochs that <see cref="GetEpochSwitchInfoBetween"/> cannot resolve.
    /// See <see cref="BaseEpochSwitchManager.FindEpochSwitchHeader"/>.
    /// </remarks>
    private BlockRoundInfo[]? GetEpochSwitchBlocksBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        List<BlockRoundInfo> epochSwitchBlocks = [];

        Hash256 iteratorHash = end.Hash!;
        ulong iteratorBlockNumber = end.Number;

        // The previous epoch is stepped into by hash, so the walk stops without having to resolve the header it
        // would have continued from
        while (iteratorBlockNumber > start.Number)
        {
            if (Tree.FindHeader(iteratorHash) is not XdcBlockHeader iterator)
            {
                return null;
            }

            if (FindEpochSwitchHeader(iterator) is not { } epochSwitchHeader)
            {
                return null;
            }

            // The switch block carries no quorum certificate, so the walk cannot step into the previous epoch.
            if (epochSwitchHeader.ExtraConsensusData?.QuorumCert?.ProposedBlockInfo is not { } parentBlock)
            {
                break;
            }

            iteratorHash = parentBlock.Hash;
            iteratorBlockNumber = epochSwitchHeader.Number;

            if (iteratorBlockNumber >= start.Number)
            {
                epochSwitchBlocks.Add(ToBlockRoundInfo(epochSwitchHeader));
            }
        }

        epochSwitchBlocks.Reverse();
        return epochSwitchBlocks.ToArray();
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

        if (FindEpochSwitchHeader(headHeader) is not { } headEpochSwitchHeader)
        {
            return null;
        }

        BlockRoundInfo headEpochSwitchBlock = ToBlockRoundInfo(headEpochSwitchHeader);

        ulong epochNumber = xdcSpec.SwitchEpoch + headEpochSwitchBlock.Round / xdcSpec.EpochLength;

        if (targetEpoch == epochNumber)
        {
            return headEpochSwitchBlock;
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
            headEpochSwitchBlock.BlockNumber.SaturatingSub(estBlockNumDiff));

        ulong closeEpochNum = 2ul;

        if (closeEpochNum >= epochNumber - targetEpoch)
        {
            XdcBlockHeader? estBlockHeader = (XdcBlockHeader?)Tree.FindHeader(estBlockNum);
            if (estBlockHeader is null)
            {
                return null;
            }
            BlockRoundInfo[]? epochSwitchBlocks = GetEpochSwitchBlocksBetween(estBlockHeader, headHeader);
            if (epochSwitchBlocks is null)
            {
                return null;
            }
            foreach (BlockRoundInfo blockInfo in epochSwitchBlocks)
            {
                ulong epochNum = xdcSpec.SwitchEpoch + blockInfo.Round / xdcSpec.EpochLength;
                if (epochNum == targetEpoch)
                {
                    return blockInfo;
                }
            }
        }

        if (!TryBinarySearchBlockByEpochNumber(targetEpoch, estBlockNum, headEpochSwitchBlock.BlockNumber, xdcSpec.SwitchBlock, xdcSpec.EpochLength, xdcSpec, out epochBlockInfo))
        {
            return null;
        }

        return epochBlockInfo;
    }
}
