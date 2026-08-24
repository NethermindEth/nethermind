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
    private LruCache<ValueHash256, Hash256> EpochSwitchHeaders { get; } = new(XdcConstants.InMemoryEpochs, nameof(EpochSwitchHeaders));

    public abstract bool IsEpochSwitchAtBlock(XdcBlockHeader header);

    public abstract bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);

    public abstract BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch);
    public abstract EpochSwitchInfo[]? GetEpochSwitchInfoBetween(XdcBlockHeader start, XdcBlockHeader end);

    /// <summary>
    /// Walks back from <paramref name="header"/> to the epoch switch header that opens its epoch.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetEpochSwitchInfo(XdcBlockHeader)"/> this needs no snapshot, so it still resolves epochs
    /// whose gap block snapshot is unavailable — the case for epochs below a fast sync pivot, where no block is ever
    /// processed. Use it wherever only the epoch switch block itself is needed, not the master node set.
    /// </remarks>
    /// <returns>
    /// The epoch switch header, or <c>null</c> when an ancestor is missing from the block tree or the epoch predates
    /// V2 consensus and so carries no round to report.
    /// </returns>
    protected XdcBlockHeader? FindEpochSwitchHeader(XdcBlockHeader header)
    {
        Hash256 headerHash = header.Hash!;
        if (EpochSwitchHeaders.TryGet(headerHash, out Hash256 cachedHash))
            return Tree.FindHeader(cachedHash) as XdcBlockHeader;

        while (!IsEpochSwitchAtBlock(header))
        {
            // The parent opens the same epoch whenever the child is not itself an epoch switch, so its entry carries over
            if (EpochSwitchHeaders.TryGet(header.ParentHash!, out Hash256 cachedParentHash))
            {
                EpochSwitchHeaders.Set(headerHash, cachedParentHash);
                return Tree.FindHeader(cachedParentHash) as XdcBlockHeader;
            }

            if (Tree.FindHeader(header.ParentHash!) is not XdcBlockHeader parent)
                return null;

            header = parent;
        }

        // Matches GetEpochSwitchInfo: below the switch block there is no consensus data, so no round to report.
        if (header.ExtraConsensusData is null && header.Number != XdcSpecProvider.GetXdcSpec(header).SwitchBlock)
            return null;

        EpochSwitchHeaders.Set(headerHash, header.Hash!);
        return header;
    }

    protected static BlockRoundInfo ToBlockRoundInfo(XdcBlockHeader epochSwitchHeader) =>
        new(epochSwitchHeader.Hash!, epochSwitchHeader.ExtraConsensusData?.BlockRound ?? 0, epochSwitchHeader.Number);

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
            if (EpochSwitches.TryGet(header.ParentHash!, out EpochSwitchInfo cached))
            {
                EpochSwitches.Set(headerHash, cached);
                return cached;
            }

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
            HashSet<Address> excluded = [.. masterNodes];
            excluded.UnionWith(penalties);

            List<Address> result = [];
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
