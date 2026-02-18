// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc;

internal class EpochSwitchManager : BaseEpochSwitchManager
{
    public EpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager)
        : base(xdcSpecProvider, tree, snapshotManager)
    {
    }

    /**
     * Determine if the given block is an epoch switch block.
    **/
    public override bool IsEpochSwitchAtBlock(XdcBlockHeader header)
    {
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(header);

        if (header.Number == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (header.ExtraConsensusData is null)
        {
            return false;
        }

        var round = header.ExtraConsensusData.BlockRound;
        var qc = header.ExtraConsensusData.QuorumCert;

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - (round % (ulong)xdcSpec.EpochLength);
        ulong epochNumber = (ulong)xdcSpec.SwitchEpoch + round / (ulong)xdcSpec.EpochLength;

        if (qc.ProposedBlockInfo.BlockNumber == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (parentRound < epochStartRound)
        {
            _round2EpochBlockInfo.Set(round, new BlockRoundInfo(header.Hash, round, header.Number));
            return true;
        }

        return false;
    }

    /**
     * Determine if an epoch switch occurs at the given round, based on the parent block.
    **/
    public override bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent)
    {
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(parent);

        ulong epochNumber = (ulong)xdcSpec.SwitchEpoch + currentRound / (ulong)xdcSpec.EpochLength;

        if (parent.Number == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (parent.ExtraConsensusData is null)
        {
            return false;
        }

        var parentRound = parent.ExtraConsensusData.BlockRound;
        if (currentRound <= parentRound)
        {
            return false;
        }

        ulong epochStartRound = currentRound - (currentRound % (ulong)xdcSpec.EpochLength);
        return parentRound < epochStartRound;
    }

    protected override Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec)
    {
        return header.PenaltiesAddress.Value.ToArray();
    }

    private EpochSwitchInfo[] GetEpochSwitchBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        var epochSwitchInfos = new List<EpochSwitchInfo>();

        Hash256 iteratorHash = end.Hash;
        long iteratorBlockNumber = end.Number;

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
        var epochSwitchInCache = new List<BlockRoundInfo>();

        for (ulong r = estRound; r < estRound + (ulong)epoch; r++)
        {
            if (_round2EpochBlockInfo.TryGet(r, out BlockRoundInfo blockInfo))
            {
                epochSwitchInCache.Add(blockInfo);
            }
        }

        if (epochSwitchInCache.Count == 1)
        {
            return epochSwitchInCache[0];
        }
        else if (epochSwitchInCache.Count == 0)
        {
            return null;
        }

        foreach (var blockInfo in epochSwitchInCache)
        {
            var header = _tree.FindHeader(blockInfo.BlockNumber);
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

    private bool TryBinarySearchBlockByEpochNumber(ulong targetEpochNumber, long start, long end, ulong switchBlock, ulong epoch, IXdcReleaseSpec xdcSpec, out BlockRoundInfo epochBlockInfo)
    {
        while (start < end)
        {
            var header = (XdcBlockHeader)_tree.FindHeader((start + end) / 2);
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
            ulong epochNum = (ulong)xdcSpec.SwitchEpoch + (header.ExtraConsensusData?.BlockRound ?? 0) / (ulong)xdcSpec.EpochLength;

            if (epochNum == targetEpochNumber)
            {
                if (header.ExtraConsensusData is null)
                {
                    epochBlockInfo = null;
                    return false;
                }

                ulong round = header.ExtraConsensusData.BlockRound;

                if (isEpochSwitch)
                {
                    epochBlockInfo = new BlockRoundInfo(header.Hash, round, header.Number);
                    return true;
                }
                else
                {
                    end = header.Number;
                    // trick to shorten the search
                    start = Math.Max(start, end - (int)(round % epoch));
                }
            }
            else if (epochNum > targetEpochNumber)
            {
                end = header.Number;
            }
            else
            {
                long nextStart = header.Number;
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

    public override EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert)
    {
        var headOfChainHeader = (XdcBlockHeader)_tree.Head.Header;

        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headOfChainHeader);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        var xdcSpec = _xdcSpecProvider.GetXdcSpec(headOfChainHeader);

        ulong epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        ulong tempTCEpoch = (ulong)xdcSpec.SwitchEpoch + epochRound / (ulong)xdcSpec.EpochLength;

        var epochBlockInfo = new BlockRoundInfo(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber);

        while (epochBlockInfo.Round > timeoutCert.Round)
        {
            tempTCEpoch--;

            // TODO seems to be bug, we should update epochBlockInfo = GetBlockByEpochNumber but not using it here.
            if (GetBlockByEpochNumber(tempTCEpoch) is null)
            {
                return null;
            }
        }

        return GetEpochSwitchInfo(epochBlockInfo.Hash);
    }

    public override BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch)
    {
        var headHeader = _tree.Head?.Header as XdcBlockHeader;
        if (headHeader is null)
        {
            return null;
        }
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(headHeader);

        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headHeader);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        ulong epochNumber = (ulong)xdcSpec.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)xdcSpec.EpochLength;

        if (targetEpoch == epochNumber)
        {
            return epochSwitchInfo.EpochSwitchBlockInfo;
        }

        if (targetEpoch > epochNumber)
        {
            return null;
        }

        if (targetEpoch < (ulong)xdcSpec.SwitchEpoch)
        {
            return null;
        }

        ulong estRound = (targetEpoch - (ulong)xdcSpec.SwitchEpoch) * (ulong)xdcSpec.EpochLength;

        var epochBlockInfo = GetBlockInfoInCache(estRound, (ulong)xdcSpec.EpochLength);
        if (epochBlockInfo is not null)
        {
            return epochBlockInfo;
        }

        var epoch = (ulong)xdcSpec.EpochLength;
        ulong estBlockNumDiff = epoch * (epochNumber - targetEpoch);
        long estBlockNum = Math.Max((long)xdcSpec.SwitchBlock, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber - (long)estBlockNumDiff);

        ulong closeEpochNum = 2ul;

        if (closeEpochNum >= epochNumber - targetEpoch)
        {
            var estBlockHeader = (XdcBlockHeader)_tree.FindHeader(estBlockNum);
            if (estBlockHeader is null)
            {
                return null;
            }
            var epochSwitchInfos = GetEpochSwitchBetween(estBlockHeader, headHeader);
            if (epochSwitchInfos is null)
            {
                return null;
            }
            foreach (var info in epochSwitchInfos)
            {
                ulong epochNum = (ulong)xdcSpec.SwitchEpoch + info.EpochSwitchBlockInfo.Round / (ulong)xdcSpec.EpochLength;
                if (epochNum == targetEpoch)
                {
                    return info.EpochSwitchBlockInfo;
                }
            }
        }

        if (!TryBinarySearchBlockByEpochNumber(targetEpoch, estBlockNum, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber, (ulong)xdcSpec.SwitchBlock, (ulong)xdcSpec.EpochLength, xdcSpec, out epochBlockInfo))
        {
            return null;
        }

        return epochBlockInfo;
    }
}
