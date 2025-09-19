// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.LinearAlgebra.Factorization;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;

namespace Nethermind.Xdc;
internal class EpochSwitchManager : IEpochSwitchManager
{
    public EpochSwitchManager(IXdcConfig xdcConfig, IBlockTree tree, ISnapshotManager snapshotManager)
    {
        XdcConfig = xdcConfig;
        Tree = tree;
        SnapshotManager = snapshotManager;
    }

    public IXdcConfig XdcConfig { get; }
    public IBlockTree Tree { get; }
    public ISnapshotManager SnapshotManager { get; }
    public ConcurrentDictionary<ulong, BlockInfo> Round2EpochBlockInfo { get; set; }
    public ConcurrentDictionary<ValueHash256, EpochSwitchInfo> EpochSwitches { get; set; }

    public bool IsEpochSwitch(XdcBlockHeader header, out ulong epochNumber)
    {
        if (header.Number == XdcConfig.SwitchBlock)
        {
            epochNumber = (ulong)header.Number / XdcConfig.Epoch;
            return true;
        }

        if (!Utils.TryGetExtraFields(header, XdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out _))
        {
            epochNumber = 0;
            return false;
        }

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - round % XdcConfig.Epoch;
        epochNumber = XdcConfig.SwitchEpoch + round / XdcConfig.Epoch;

        if (qc.ProposedBlockInfo.Number == XdcConfig.SwitchBlock)
        {
            return true;
        }

        if (parentRound < epochStartRound)
        {
            Round2EpochBlockInfo[round] = new BlockInfo(header.Hash, round, header.Number);
            return true;
        }

        return false;
    }

    public bool IsEpochSwitchAtBlock(XdcBlockHeader header, out ulong epochNumber)
    {
        if (header.Number == XdcConfig.SwitchBlock)
        {
            epochNumber = (ulong)header.Number / XdcConfig.Epoch;
            return true;
        }

        if (!Utils.TryGetExtraFields(header, XdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out _))
        {
            epochNumber = 0;
            return false;
        }

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - (round % XdcConfig.Epoch);
        epochNumber = XdcConfig.SwitchEpoch + round / XdcConfig.Epoch;

        if (qc.ProposedBlockInfo.Number == XdcConfig.SwitchBlock)
        {
            return true;
        }

        if (parentRound < epochStartRound)
        {
            Round2EpochBlockInfo[round] = new BlockInfo(header.Hash, round, header.Number);
            return true;
        }

        return false;
    }

    public bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent, out ulong epochNumber)
    {
        epochNumber = (XdcConfig.SwitchEpoch + currentRound) / XdcConfig.Epoch;

        if (parent.Number == XdcConfig.SwitchBlock)
        {
            return true;
        }

        if (!Utils.TryGetExtraFields(parent, XdcConfig.SwitchBlock, out _, out ulong parentRound, out _))
        {
            return false;
        }

        if (currentRound <= parentRound)
        {
            return false;
        }

        ulong epochStartRound = currentRound - (currentRound % XdcConfig.Epoch);
        return parentRound < epochStartRound;
    }

    public (ulong currentCheckpointNumber, ulong epochNumber)? GetCurrentEpochNumbers(ulong blockNumber) 
    {
        var header = (XdcBlockHeader)Tree.FindHeader((long)blockNumber);
        EpochSwitchInfo epochSwitchInfo;
        if ((epochSwitchInfo = GetEpochSwitchInfo(header, header.Hash)) is null)
        {
            return null;
        }

        return ((ulong)epochSwitchInfo.EpochSwitchBlockInfo.Number, XdcConfig.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / XdcConfig.Epoch);
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

            if ((epochSwitchInfo = GetEpochSwitchInfo(iteratorHeader, iteratorHash)) is null)
            {
                return null;
            }

            iteratorHeader = null;

            if (epochSwitchInfo.EpochSwitchParentBlockInfo is null)
            {
                break;
            }

            iteratorHash = epochSwitchInfo.EpochSwitchParentBlockInfo.Hash;
            iteratorBlockNumber = epochSwitchInfo.EpochSwitchParentBlockInfo.Number;

            if (iteratorBlockNumber >= start.Number)
            {
                epochSwitchInfos.Add(epochSwitchInfo);
            }
        }

        epochSwitchInfos.Reverse();
        return epochSwitchInfos.ToArray();
    }

    public EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header, Hash256 hash)
    {
        if (EpochSwitches.TryGetValue(header.Hash, out var epochSwitchInfo) && epochSwitchInfo is not null)
        {
            return null;
        }

        XdcBlockHeader h = header;

        if (h is null)
        {
            h = (XdcBlockHeader)Tree.FindHeader(hash);
            if (h is null)
            {
                return null;
            }
        }

        if (IsEpochSwitchAtBlock(h, out _))
        {
            if (h.Number == 0)
            {
                // genesis handling
                epochSwitchInfo = new EpochSwitchInfo([], [], Utils.GetMasternodesFromGenesisHeader(Tree, header), new BlockInfo(hash, 0, h.Number), null);
                EpochSwitches[header.Hash] = epochSwitchInfo;
                return epochSwitchInfo;
            }

            if (!Utils.TryGetExtraFields(h, XdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out Address[] masterNodes))
            {
                return null;
            }

            if (!SnapshotManager.TryGetSnapshot(h, out Snapshot snap))
            {
                return null;
            }

            var penalties = Utils.ExtractAddressFromBytes(h.Penalties);
            var candidates = snap.NextEpochCandidates;

            var stanbyNodes = new Address[0];

            if (masterNodes.Length != candidates.Length)
            {
                stanbyNodes = candidates;
                stanbyNodes = Utils.RemoveItemFromArray(stanbyNodes, masterNodes);
                stanbyNodes = Utils.RemoveItemFromArray(stanbyNodes, penalties);
            }

            epochSwitchInfo = new EpochSwitchInfo(penalties, stanbyNodes, Utils.GetMasternodesFromGenesisHeader(Tree, header), new BlockInfo(h.Hash, round, h.Number), null);

            if (qc is not null)
            {
                epochSwitchInfo.EpochSwitchParentBlockInfo = qc.ProposedBlockInfo;
            }

            EpochSwitches[header.Hash] = epochSwitchInfo;
            return epochSwitchInfo;
        }

        return EpochSwitches[hash] = GetEpochSwitchInfo(null, h.ParentHash);
    }

    public EpochSwitchInfo? GetPreviousEpochSwitchInfoByHash(Hash256 parentHash, int limit)
    {
        EpochSwitchInfo epochSwitchInfo;

        if ((epochSwitchInfo = GetEpochSwitchInfo(null, parentHash)) is null)
        {
            return null;
        }

        for (int i = 0; i < limit; i++)
        {
            if ((epochSwitchInfo = GetEpochSwitchInfo(null, epochSwitchInfo.EpochSwitchParentBlockInfo.Hash)) is null)
            {
                return null;
            }
        }

        return epochSwitchInfo;
    }

    internal bool TryGetBlockByEpochNumber(ulong tempTCEpoch, out Types.BlockInfo epochBlockInfo)
    {
        var headHeader = Tree.Head.Header as XdcBlockHeader;
        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headHeader, headHeader.Hash);
        if (epochSwitchInfo is null)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong epochRound = (ulong)XdcConfig.SwitchBlock + epochSwitchInfo.EpochSwitchBlockInfo.Round / XdcConfig.Epoch;

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

        if( tempTCEpoch < XdcConfig.SwitchEpoch)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong estRound = (tempTCEpoch - XdcConfig.SwitchEpoch) * XdcConfig.Epoch;

        if (TryGetBlockInfoInCache(estRound, out epochBlockInfo))
        {
            return true;
        }

        var epoch = (ulong)XdcConfig.Epoch;
        ulong estBlockNumDiff = epoch * (epochRound - tempTCEpoch);
        long estBlockNum = epochSwitchInfo.EpochSwitchBlockInfo.Number - (long)estBlockNumDiff;

        if (estBlockNum < XdcConfig.SwitchBlock)
        {
            estBlockNum = XdcConfig.SwitchBlock;
        }

        ulong closeEpochNum = 2ul;

        if(closeEpochNum >= epochRound - tempTCEpoch)
        {
            var estBlockHeader = (XdcBlockHeader)Tree.FindHeader(estBlockNum);
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
                ulong epochNum = XdcConfig.SwitchEpoch + info.EpochSwitchBlockInfo.Round / XdcConfig.Epoch;
                if (epochNum == tempTCEpoch)
                {
                    epochBlockInfo = info.EpochSwitchBlockInfo;
                    return true;
                }
            }
        }

        return TryBinarySearchBlockByEpochNumber(tempTCEpoch, estBlockNum, epochSwitchInfo.EpochSwitchBlockInfo.Number, out epochBlockInfo);
    }

    private bool TryGetBlockInfoInCache(ulong estRound, out BlockInfo epochBlockInfo)
    {
        var epochSwitchInCache = new List<BlockInfo>();

        for(ulong r = estRound; r < estRound + (ulong)XdcConfig.Epoch; r++)
        {
            if (Round2EpochBlockInfo.TryGetValue(r, out BlockInfo blockInfo) && blockInfo is not null)
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
            var header = Tree.FindHeader(blockInfo.Number);
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

    private bool TryBinarySearchBlockByEpochNumber(ulong tempTCEpoch, long start, long end, out BlockInfo epochBlockInfo)
    {
        while (start < end)
        {
            var header = (XdcBlockHeader)Tree.FindHeader((start + end) / 2);
            if (header is null)
            {
                epochBlockInfo = null;
                return false;
            }

            bool isEpochSwitch = IsEpochSwitch(header, out ulong epochNum);

            if (epochNum == tempTCEpoch)
            {
                if (!Utils.TryGetExtraFields(header, XdcConfig.SwitchBlock, out _, out ulong round, out _))
                {
                    epochBlockInfo = null;
                    return false;
                }

                if (isEpochSwitch)
                {
                    epochBlockInfo = new BlockInfo(header.Hash, round, header.Number);
                    return true;
                } else
                {
                    end = header.Number;
                    // trick to shorten the search
                    ulong estStart = (ulong)end - (round % XdcConfig.Epoch);
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

    public EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCert timeoutCert)
    {
        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo((XdcBlockHeader)Tree.Head.Header, Tree.Head.Header.Hash);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        ulong epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        ulong tempTCEpoch = XdcConfig.SwitchEpoch + epochRound / XdcConfig.Epoch;

        var epochBlockInfo = new BlockInfo(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound, epochSwitchInfo.EpochSwitchBlockInfo.Number);

        while (epochBlockInfo.Round > timeoutCert.Round)
        {
            tempTCEpoch--;

            if (!TryGetBlockByEpochNumber(tempTCEpoch, out epochBlockInfo))
            {
                return null;
            }
        }

        return GetEpochSwitchInfo(null, epochBlockInfo.Hash);
    }
}
