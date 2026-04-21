// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal abstract class BaseEpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager) : IEpochSwitchManager
{
    protected ISpecProvider XdcSpecProvider { get; } = xdcSpecProvider;
    protected IBlockTree Tree { get; } = tree;
    protected ISnapshotManager SnapshotManager { get; } = snapshotManager;
    protected LruCache<ValueHash256, EpochSwitchInfo> EpochSwitches { get; } = new(XdcConstants.InMemoryEpochs, nameof(EpochSwitches));

    public abstract bool IsEpochSwitchAtBlock(XdcBlockHeader header);

    public abstract bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);

    public abstract BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch);

    public EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header)
    {
        Hash256 headerHash = header.Hash;
        if (EpochSwitches.TryGet(headerHash, out EpochSwitchInfo epochSwitchInfo))
        {
            return epochSwitchInfo;
        }

        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(header);

        while (!IsEpochSwitchAtBlock(header))
        {
            header = (XdcBlockHeader)(Tree.FindHeader(header.ParentHash!) ?? throw new InvalidOperationException($"Parent block {header.ParentHash} not found while walking to epoch switch"));
        }

        Address[] masterNodes;

        if (header.Number == xdcSpec.SwitchBlock)
        {
            masterNodes = xdcSpec.GenesisMasterNodes;
        }
        else
        {
            if (header.ExtraConsensusData is null)
            {
                return null;
            }

            masterNodes = header.ValidatorsAddress is null
                ? throw new InvalidOperationException($"ValidatorsAddress is null on epoch-switch block {header.Number}")
                : [.. header.ValidatorsAddress.Value];
        }

        Snapshot snap = SnapshotManager.GetSnapshotByBlockNumber(header.Number, xdcSpec);
        if (snap is null)
        {
            return null;
        }

        Address[] penalties = ResolvePenalties(header, snap);
        Address[] candidates = snap.NextEpochCandidates;

        Address[] standbyNodes = [];

        if (masterNodes.Length != candidates.Length)
        {
            HashSet<Address> excluded = new(masterNodes);
            excluded.UnionWith(penalties);

            List<Address> result = new();
            foreach (Address candidate in candidates)
            {
                if (excluded.Add(candidate))
                    result.Add(candidate);
            }
            standbyNodes = result.ToArray();
        }

        epochSwitchInfo = new EpochSwitchInfo(masterNodes, standbyNodes, penalties, new BlockRoundInfo(header.Hash, header.ExtraConsensusData?.BlockRound ?? 0, header.Number));

        if (header.ExtraConsensusData?.QuorumCert is not null)
        {
            epochSwitchInfo.EpochSwitchParentBlockInfo = header.ExtraConsensusData.QuorumCert.ProposedBlockInfo;
        }

        EpochSwitches.Set(headerHash, epochSwitchInfo);
        return epochSwitchInfo;
    }

    protected abstract Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot);

    public EpochSwitchInfo? GetEpochSwitchInfo(Hash256 hash)
    {
        if (EpochSwitches.TryGet(hash, out EpochSwitchInfo epochSwitchInfo))
        {
            return epochSwitchInfo;
        }

        XdcBlockHeader? h = (XdcBlockHeader?)Tree.FindHeader(hash);
        if (h is null) return null;

        return GetEpochSwitchInfo(h);
    }

    protected abstract ulong GetCurrentEpochNumber(EpochSwitchInfo epochSwitchInfo, IXdcReleaseSpec xdcSpec);

    public EpochSwitchInfo? GetEpochSwitchInfo(ulong round)
    {
        XdcBlockHeader? headOfChainHeader = (XdcBlockHeader?)Tree.Head?.Header;
        if (headOfChainHeader is null) return null;

        EpochSwitchInfo epochSwitchInfo = GetEpochSwitchInfo(headOfChainHeader);
        if (epochSwitchInfo is null)
        {
            return null;
        }

        IXdcReleaseSpec xdcSpec = XdcSpecProvider.GetXdcSpec(headOfChainHeader);

        ulong epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        ulong tempTCEpoch = GetCurrentEpochNumber(epochSwitchInfo, xdcSpec);

        BlockRoundInfo epochBlockInfo = new(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound, epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber);

        while (epochBlockInfo.Round > round)
        {
            tempTCEpoch--;
            epochBlockInfo = GetBlockByEpochNumber(tempTCEpoch);
            if (epochBlockInfo is null)
            {
                return null;
            }
        }

        return GetEpochSwitchInfo(epochBlockInfo.Hash);
    }


    public EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert) => GetEpochSwitchInfo(timeoutCert.Round);
}
