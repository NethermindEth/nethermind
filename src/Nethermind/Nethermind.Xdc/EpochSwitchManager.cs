// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.LinearAlgebra.Factorization;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Xdc;
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
    private ConcurrentDictionary<ulong, BlockInfo> _round2EpochBlockInfo { get; set; } = new();
    private ConcurrentDictionary<ValueHash256, EpochSwitchInfo> _epochSwitches { get; set; } = new();

    public bool IsEpochSwitchAtBlock(XdcBlockHeader header, out ulong epochNumber)
    {
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(header);

        if (header.Number == xdcSpec.SwitchBlock)
        {
            epochNumber = (ulong)header.Number / (ulong)xdcSpec.EpochLength;
            return true;
        }

        if(header.ExtraConsensusData is null)
        {
            epochNumber = 0;
            return false;
        }

        var round = header.ExtraConsensusData.CurrentRound;
        var qc = header.ExtraConsensusData.QuorumCert;

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - (round % (ulong)xdcSpec.EpochLength);
        epochNumber = (ulong)xdcSpec.SwitchEpoch + round / (ulong)xdcSpec.EpochLength;

        if (qc.ProposedBlockInfo.BlockNumber == xdcSpec.SwitchBlock)
        {
            return true;
        }

        if (parentRound < epochStartRound)
        {
            _round2EpochBlockInfo[round] = new BlockInfo(header.Hash, round, header.Number);
            return true;
        }

        return false;
    }

    public bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent, out ulong epochNumber)
    {
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(parent);

        epochNumber = ((ulong)xdcSpec.SwitchEpoch + currentRound) / (ulong)xdcSpec.EpochLength;

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

    public (ulong currentCheckpointNumber, ulong epochNumber)? GetCurrentEpochNumbers(ulong blockNumber) 
    {
        var header = (XdcBlockHeader)_tree.FindHeader((long)blockNumber);
        if (header is null)
        {
            return null;
        }

        var xdcSpec = _xdcSpecProvider.GetXdcSpec(header);

        EpochSwitchInfo epochSwitchInfo;
        if ((epochSwitchInfo = GetEpochSwitchInfo(header)) is null)
        {
            return null;
        }

        return ((ulong)epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber, (ulong)xdcSpec.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)xdcSpec.EpochLength);
    }

    public EpochSwitchInfo[] GetEpochSwitchBetween(XdcBlockHeader start, XdcBlockHeader end)
    {
        var epochSwitchInfos = new List<EpochSwitchInfo>();

        XdcBlockHeader iteratorHeader = end;
        Hash256 iteratorHash = end.Hash;
        long iteratorBlockNumber = end.Number;

        while (iteratorBlockNumber > start.Number)
        {
            EpochSwitchInfo epochSwitchInfo;

            if ((epochSwitchInfo = GetEpochSwitchInfo(iteratorHeader)) is null)
            {
                return null;
            }

            iteratorHeader = null;

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

    public EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header)
    {
        var xdcSpec = _xdcSpecProvider.GetXdcSpec(header);

        while (!IsEpochSwitchAtBlock(header, out _))
        {
            header = (XdcBlockHeader)_tree.FindHeader(header.ParentHash);
        }

        if (header.Number == 0)
        {
            // genesis handling
            return _epochSwitches[header.Hash] = new EpochSwitchInfo(Utils.GetMasternodesFromGenesisHeader(_tree, header), [], [], new BlockRoundInfo(header.Hash, 0, header.Number));
        }

        if (!Utils.TryGetExtraFields(header, (long)xdcSpec.SwitchBlock, out ExtraFieldsV2 consensusData, out Address[] masterNodes))
        {
            return null;
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
            stanbyNodes = Utils.RemoveItemFromArray(candidates, masterNodes);
            stanbyNodes = Utils.RemoveItemFromArray(stanbyNodes, penalties);
        }

        var epochSwitchInfo = new EpochSwitchInfo(masterNodes, stanbyNodes, penalties, new BlockRoundInfo(header.Hash, consensusData?.CurrentRound ?? 0, header.Number));

        if (consensusData?.QuorumCert is not null)
        {
            epochSwitchInfo.EpochSwitchParentBlockInfo = consensusData.QuorumCert.ProposedBlockInfo;
        }

        return _epochSwitches[header.Hash] = epochSwitchInfo;
    }

    public EpochSwitchInfo? GetEpochSwitchInfo(Hash256 hash)
    {
        if (_epochSwitches.TryGetValue(hash, out var epochSwitchInfo) && epochSwitchInfo is not null)
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

    public EpochSwitchInfo? GetPreviousEpochSwitchInfoByHash(Hash256 blockHash, int limit)
    {
        EpochSwitchInfo epochSwitchInfo;

        if ((epochSwitchInfo = GetEpochSwitchInfo(blockHash)) is null)
        {
            return null;
        }

        for (int i = 0; i < limit; i++)
        {
            if ((epochSwitchInfo = GetEpochSwitchInfo(epochSwitchInfo.EpochSwitchParentBlockInfo.Hash)) is null)
            {
                return null;
            }
        }

        return epochSwitchInfo;
    }

    internal bool TryGetBlockByEpochNumber(ulong tempTCEpoch, out Types.BlockRoundInfo epochBlockInfo)
    {
        var headHeader = _tree.Head.Header as XdcBlockHeader;

        var xdcSpec = _xdcSpecProvider.GetXdcSpec(headHeader);

        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headHeader);
        if (epochSwitchInfo is null)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong epochRound = (ulong)xdcSpec.SwitchBlock + epochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)xdcSpec.EpochLength;

        if (tempTCEpoch == epochRound)
        {
            epochBlockInfo = epochSwitchInfo.EpochSwitchBlockInfo;
            return true;
        }

        if(tempTCEpoch > epochRound)
        {
            epochBlockInfo = null;
            return false;
        }

        if(tempTCEpoch < (ulong)xdcSpec.SwitchEpoch)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong estRound = (tempTCEpoch - (ulong)xdcSpec.SwitchEpoch) * (ulong)xdcSpec.EpochLength;

        if (TryGetBlockInfoInCache(estRound, (ulong)xdcSpec.EpochLength, out epochBlockInfo))
        {
            return true;
        }

        var epoch = (ulong)xdcSpec.EpochLength;
        ulong estBlockNumDiff = epoch * (epochRound - tempTCEpoch);
        long estBlockNum = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber - (long)estBlockNumDiff;

        if (estBlockNum < xdcSpec.SwitchBlock)
        {
            estBlockNum = (long)xdcSpec.SwitchBlock;
        }

        ulong closeEpochNum = 2ul;

        if(closeEpochNum >= epochRound - tempTCEpoch)
        {
            var estBlockHeader = (XdcBlockHeader)_tree.FindHeader(estBlockNum);
            if (estBlockHeader is null)
            {
                epochBlockInfo = null;
                return false;
            }
            var epochSwitchInfos = GetEpochSwitchBetween(estBlockHeader, headHeader);
            if (epochSwitchInfos is null)
            {
                return false;
            }
            foreach (var info in epochSwitchInfos)
            {
                ulong epochNum = (ulong)xdcSpec.SwitchEpoch + info.EpochSwitchBlockInfo.Round / (ulong)xdcSpec.EpochLength;
                if (epochNum == tempTCEpoch)
                {
                    epochBlockInfo = info.EpochSwitchBlockInfo;
                    return true;
                }
            }
        }

        return TryBinarySearchBlockByEpochNumber(tempTCEpoch, estBlockNum, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber, (ulong)xdcSpec.SwitchBlock, (ulong)xdcSpec.EpochLength, out epochBlockInfo);
    }

    private bool TryGetBlockInfoInCache(ulong estRound, ulong epoch, out BlockInfo epochBlockInfo)
    {
        var epochSwitchInCache = new List<BlockInfo>();

        for(ulong r = estRound; r < estRound + (ulong)epoch; r++)
        {
            if (_round2EpochBlockInfo.TryGetValue(r, out BlockInfo blockInfo) && blockInfo is not null)
            {
                epochSwitchInCache.Add(blockInfo);
            }
        }

        if (epochSwitchInCache.Count == 1)
        {
            epochBlockInfo = epochSwitchInCache[0];
            return true;
        } else if (epochSwitchInCache.Count == 0)
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

    private bool TryBinarySearchBlockByEpochNumber(ulong tempTCEpoch, long start, long end, ulong switchBlock, ulong epoch, out BlockInfo epochBlockInfo)
    {
        while (start < end)
        {
            var header = (XdcBlockHeader)_tree.FindHeader((start + end) / 2);
            if (header is null)
            {
                epochBlockInfo = null;
                return false;
            }

            bool isEpochSwitch = IsEpochSwitchAtBlock(header, out ulong epochNum);

            if (epochNum == tempTCEpoch)
            {
                if (!Utils.TryGetExtraFields(header, (long)switchBlock, out var extraFieldsV2, out _))
                {
                    epochBlockInfo = null;
                    return false;
                }

                ulong round = extraFieldsV2.CurrentRound;

                if (isEpochSwitch)
                {
                    epochBlockInfo = new BlockInfo(header.Hash, round, header.Number);
                    return true;
                } else
                {
                    end = header.Number;
                    // trick to shorten the search
                    ulong estStart = (ulong)end - (round % epoch);
                    if (start < (long)estStart)
                    {
                        start = (long)estStart;
                    }
                }
            } else if (epochNum > tempTCEpoch)
            {
                end = header.Number;
            } else if (epochNum < tempTCEpoch)
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
        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo((XdcBlockHeader)_tree.Head.Header);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        var xdcSpec = _xdcSpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head.Header);

        ulong epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        ulong tempTCEpoch = (ulong)xdcSpec.SwitchEpoch + epochRound / (ulong)xdcSpec.EpochLength;

        var epochBlockInfo = new BlockInfo(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber);

        while (epochBlockInfo.Round > timeoutCert.Round)
        {
            tempTCEpoch--;

            if (!TryGetBlockByEpochNumber(tempTCEpoch, out epochBlockInfo))
            {
                return null;
            }
        }

        return GetEpochSwitchInfo(epochBlockInfo.Hash);
    }

    public BlockInfo? GetBlockByEpochNumber(ulong epochNumber)
    {
        if (TryGetBlockByEpochNumber(epochNumber, out var epochBlockInfo))
        {
            return epochBlockInfo;
        }
        return null;
    }
}
