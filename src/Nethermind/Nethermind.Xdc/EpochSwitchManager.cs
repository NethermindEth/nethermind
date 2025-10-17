// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.LinearAlgebra.Factorization;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdSharp.Unsafe;
using BlockInfo = Nethermind.Xdc.Types.BlockRoundInfo;

namespace Nethermind.Xdc;
internal class EpochSwitchManager : IEpochSwitchManager
{
    public EpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager)
    {
        _xdcSpecProvider = xdcSpecProvider;
        _tree = tree;
        _snapshotManager = snapshotManager;
    }

    private ISpecProvider _xdcSpecProvider { get; }
    private IBlockTree _tree { get; }
    private ISnapshotManager _snapshotManager { get; }
    private LruCache<ulong, BlockInfo> _round2EpochBlockInfo { get; set; } = new(XdcConstants.InMemoryRound2Epochs, nameof(_round2EpochBlockInfo));
    private LruCache<ValueHash256, EpochSwitchInfo> _epochSwitches { get; set; } = new(XdcConstants.InMemoryEpochs, nameof(_epochSwitches));

    /**
     * Determine if the given block is an epoch switch block.
    **/
    public bool IsEpochSwitchAtBlock(XdcBlockHeader header)
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

        var round = header.ExtraConsensusData.CurrentRound;
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
            _round2EpochBlockInfo.Set(round, new BlockInfo(header.Hash, round, header.Number));
            return true;
        }

        return false;
    }

    /**
     * Determine if an epoch switch occurs at the given round, based on the parent block.
    **/
    public bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent)
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

        var parentRound = parent.ExtraConsensusData.CurrentRound;
        if (currentRound <= parentRound)
        {
            return false;
        }

        ulong epochStartRound = currentRound - (currentRound % (ulong)xdcSpec.EpochLength);
        return parentRound < epochStartRound;
    }

    public EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header)
    {
        if (_epochSwitches.TryGet(header.Hash, out var epochSwitchInfo))
        {
            return epochSwitchInfo;
        }

        var xdcSpec = _xdcSpecProvider.GetXdcSpec(header);

        while (!IsEpochSwitchAtBlock(header))
        {
            header = (XdcBlockHeader)_tree.FindHeader(header.ParentHash);
        }

        Address[] masterNodes;

        if (header.Number == xdcSpec.SwitchBlock)
        {
            masterNodes = XdcExtensions.ExtractAddresses(header.ExtraData[XdcConstants.ExtraVanity..^XdcConstants.ExtraSeal]).Value.ToArray();
        } else
        {
            if (header.ExtraConsensusData is null)
            {
                return null;
            }

            masterNodes = header.ValidatorsAddress.Value.ToArray();
        }


        var snap = _snapshotManager.GetSnapshot(header.Hash);
        if (snap is null)
        {
            return null;
        }

        Address[] penalties = header.PenaltiesAddress.Value.ToArray();
        Address[] candidates = snap.NextEpochCandidates;

        var stanbyNodes = new Address[0];

        if (masterNodes.Length != candidates.Length)
        {
            stanbyNodes = candidates
                .Except(masterNodes)
                .Except(penalties)
                .ToArray();
        }

        epochSwitchInfo = new EpochSwitchInfo(masterNodes, stanbyNodes, penalties, new BlockRoundInfo(header.Hash, header.ExtraConsensusData?.CurrentRound ?? 0, header.Number));

        if (header.ExtraConsensusData?.QuorumCert is not null)
        {
            epochSwitchInfo.EpochSwitchParentBlockInfo = header.ExtraConsensusData.QuorumCert.ProposedBlockInfo;
        }


        _epochSwitches.Set(header.Hash, epochSwitchInfo);
        return epochSwitchInfo;
    }

    public EpochSwitchInfo? GetEpochSwitchInfo(Hash256 hash)
    {
        if (_epochSwitches.TryGet(hash, out var epochSwitchInfo))
        {
            return epochSwitchInfo;
        }

        XdcBlockHeader h = (XdcBlockHeader)_tree.FindHeader(hash);
        if (h is null)
        {
            return null;
        }

        return GetEpochSwitchInfo(h);
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

    private bool TryGetBlockInfoInCache(ulong estRound, ulong epoch, out BlockInfo epochBlockInfo)
    {
        var epochSwitchInCache = new List<BlockInfo>();

        for (ulong r = estRound; r < estRound + (ulong)epoch; r++)
        {
            if (_round2EpochBlockInfo.TryGet(r, out BlockInfo blockInfo))
            {
                epochSwitchInCache.Add(blockInfo);
            }
        }

        if (epochSwitchInCache.Count == 1)
        {
            epochBlockInfo = epochSwitchInCache[0];
            return true;
        }
        else if (epochSwitchInCache.Count == 0)
        {
            epochBlockInfo = null;
            return false;
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
                epochBlockInfo = blockInfo;
                return true;
            }
        }

        epochBlockInfo = null;
        return false;
    }

    private bool TryBinarySearchBlockByEpochNumber(ulong targetEpochNumber, long start, long end, ulong switchBlock, ulong epoch, IXdcReleaseSpec xdcSpec, out BlockInfo epochBlockInfo)
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
            ulong epochNum = (ulong)xdcSpec.SwitchEpoch + (header.ExtraConsensusData?.CurrentRound ?? 0) / (ulong)xdcSpec.EpochLength;

            if (epochNum == targetEpochNumber)
            {
                if (header.ExtraConsensusData is null)
                {
                    epochBlockInfo = null;
                    return false;
                }

                ulong round = header.ExtraConsensusData.CurrentRound;

                if (isEpochSwitch)
                {
                    epochBlockInfo = new BlockInfo(header.Hash, round, header.Number);
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
            else if (epochNum < targetEpochNumber)
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

    public EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert)
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

        var epochBlockInfo = new BlockInfo(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber);

        while (epochBlockInfo.Round > timeoutCert.Round)
        {
            tempTCEpoch--;

            if (GetBlockByEpochNumber(tempTCEpoch) is null)
            {
                return null;
            }
        }

        return GetEpochSwitchInfo(epochBlockInfo.Hash);
    }

    public BlockInfo? GetBlockByEpochNumber(ulong targetEpoch)
    {
        var headHeader = _tree.Head.Header as XdcBlockHeader;

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

        if (TryGetBlockInfoInCache(estRound, (ulong)xdcSpec.EpochLength, out var epochBlockInfo))
        {
            return epochBlockInfo;
        }

        var epoch = (ulong)xdcSpec.EpochLength;
        ulong estBlockNumDiff = epoch * (epochNumber - targetEpoch);
        long estBlockNum = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber - (long)estBlockNumDiff;

        if (estBlockNum < xdcSpec.SwitchBlock)
        {
            estBlockNum = (long)xdcSpec.SwitchBlock;
        }

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

        if(!TryBinarySearchBlockByEpochNumber(targetEpoch, estBlockNum, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber, (ulong)xdcSpec.SwitchBlock, (ulong)xdcSpec.EpochLength, xdcSpec, out epochBlockInfo))
        {
            return null;
        }

        return epochBlockInfo;
    }
}
