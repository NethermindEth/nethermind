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
        _xdcConfig = xdcConfig;
        _tree = tree;
        _snapshotManager = snapshotManager;
    }

    private IXdcConfig _xdcConfig { get; }
    private IBlockTree _tree { get; }
    private ISnapshotManager _snapshotManager { get; }
    private ConcurrentDictionary<ulong, BlockInfo> _round2EpochBlockInfo { get; set; }
    private ConcurrentDictionary<ValueHash256, EpochSwitchInfo> _epochSwitches { get; set; }

    public bool IsEpochSwitch(XdcBlockHeader header, out ulong epochNumber)
    {
        if (header.Number == _xdcConfig.SwitchBlock)
        {
            epochNumber = (ulong)header.Number / _xdcConfig.Epoch;
            return true;
        }

        if (!Utils.TryGetExtraFields(header, _xdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out _))
        {
            epochNumber = 0;
            return false;
        }

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - round % _xdcConfig.Epoch;
        epochNumber = _xdcConfig.SwitchEpoch + round / _xdcConfig.Epoch;

        if (qc.ProposedBlockInfo.Number == _xdcConfig.SwitchBlock)
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

    public bool IsEpochSwitchAtBlock(XdcBlockHeader header, out ulong epochNumber)
    {
        if (header.Number == _xdcConfig.SwitchBlock)
        {
            epochNumber = (ulong)header.Number / _xdcConfig.Epoch;
            return true;
        }

        if (!Utils.TryGetExtraFields(header, _xdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out _))
        {
            epochNumber = 0;
            return false;
        }

        ulong parentRound = qc.ProposedBlockInfo.Round;
        ulong epochStartRound = round - (round % _xdcConfig.Epoch);
        epochNumber = _xdcConfig.SwitchEpoch + round / _xdcConfig.Epoch;

        if (qc.ProposedBlockInfo.Number == _xdcConfig.SwitchBlock)
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
        epochNumber = (_xdcConfig.SwitchEpoch + currentRound) / _xdcConfig.Epoch;

        if (parent.Number == _xdcConfig.SwitchBlock)
        {
            return true;
        }

        if (!Utils.TryGetExtraFields(parent, _xdcConfig.SwitchBlock, out _, out ulong parentRound, out _))
        {
            return false;
        }

        if (currentRound <= parentRound)
        {
            return false;
        }

        ulong epochStartRound = currentRound - (currentRound % _xdcConfig.Epoch);
        return parentRound < epochStartRound;
    }

    public (ulong currentCheckpointNumber, ulong epochNumber)? GetCurrentEpochNumbers(ulong blockNumber) 
    {
        var header = (XdcBlockHeader)_tree.FindHeader((long)blockNumber);
        EpochSwitchInfo epochSwitchInfo;
        if ((epochSwitchInfo = GetEpochSwitchInfo(header, header.Hash)) is null)
        {
            return null;
        }

        return ((ulong)epochSwitchInfo.EpochSwitchBlockInfo.Number, _xdcConfig.SwitchEpoch + epochSwitchInfo.EpochSwitchBlockInfo.Round / _xdcConfig.Epoch);
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
        if (_epochSwitches.TryGetValue(header.Hash, out var epochSwitchInfo) && epochSwitchInfo is not null)
        {
            return null;
        }

        XdcBlockHeader h = header;

        if (h is null)
        {
            h = (XdcBlockHeader)_tree.FindHeader(hash);
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
                epochSwitchInfo = new EpochSwitchInfo([], [], Utils.GetMasternodesFromGenesisHeader(_tree, header), new BlockInfo(hash, 0, h.Number), null);
                _epochSwitches[header.Hash] = epochSwitchInfo;
                return epochSwitchInfo;
            }

            if (!Utils.TryGetExtraFields(h, _xdcConfig.SwitchBlock, out QuorumCert qc, out ulong round, out Address[] masterNodes))
            {
                return null;
            }

            if (!_snapshotManager.TryGetSnapshot(h, out Snapshot snap))
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

            epochSwitchInfo = new EpochSwitchInfo(penalties, stanbyNodes, Utils.GetMasternodesFromGenesisHeader(_tree, header), new BlockInfo(h.Hash, round, h.Number), null);

            if (qc is not null)
            {
                epochSwitchInfo.EpochSwitchParentBlockInfo = qc.ProposedBlockInfo;
            }

            _epochSwitches[header.Hash] = epochSwitchInfo;
            return epochSwitchInfo;
        }

        return _epochSwitches[hash] = GetEpochSwitchInfo(null, h.ParentHash);
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
        var headHeader = _tree.Head.Header as XdcBlockHeader;
        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headHeader, headHeader.Hash);
        if (epochSwitchInfo is null)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong epochRound = (ulong)_xdcConfig.SwitchBlock + epochSwitchInfo.EpochSwitchBlockInfo.Round / _xdcConfig.Epoch;

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

        if( tempTCEpoch < _xdcConfig.SwitchEpoch)
        {
            epochBlockInfo = null;
            return false;
        }

        ulong estRound = (tempTCEpoch - _xdcConfig.SwitchEpoch) * _xdcConfig.Epoch;

        if (TryGetBlockInfoInCache(estRound, out epochBlockInfo))
        {
            return true;
        }

        var epoch = (ulong)_xdcConfig.Epoch;
        ulong estBlockNumDiff = epoch * (epochRound - tempTCEpoch);
        long estBlockNum = epochSwitchInfo.EpochSwitchBlockInfo.Number - (long)estBlockNumDiff;

        if (estBlockNum < _xdcConfig.SwitchBlock)
        {
            estBlockNum = _xdcConfig.SwitchBlock;
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
                ulong epochNum = _xdcConfig.SwitchEpoch + info.EpochSwitchBlockInfo.Round / _xdcConfig.Epoch;
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

        for(ulong r = estRound; r < estRound + (ulong)_xdcConfig.Epoch; r++)
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
            var header = _tree.FindHeader(blockInfo.Number);
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
            var header = (XdcBlockHeader)_tree.FindHeader((start + end) / 2);
            if (header is null)
            {
                epochBlockInfo = null;
                return false;
            }

            bool isEpochSwitch = IsEpochSwitch(header, out ulong epochNum);

            if (epochNum == tempTCEpoch)
            {
                if (!Utils.TryGetExtraFields(header, _xdcConfig.SwitchBlock, out _, out ulong round, out _))
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
                    ulong estStart = (ulong)end - (round % _xdcConfig.Epoch);
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
        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo((XdcBlockHeader)_tree.Head.Header, _tree.Head.Header.Hash);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        ulong epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        ulong tempTCEpoch = _xdcConfig.SwitchEpoch + epochRound / _xdcConfig.Epoch;

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
