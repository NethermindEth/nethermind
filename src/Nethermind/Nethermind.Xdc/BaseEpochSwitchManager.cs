// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Linq;

namespace Nethermind.Xdc;

internal abstract class BaseEpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager) : IEpochSwitchManager
{
    protected ISpecProvider XdcSpecProvider { get; } = xdcSpecProvider;
    protected IBlockTree Tree { get; } = tree;
    protected ISnapshotManager SnapshotManager { get; } = snapshotManager;
    protected LruCache<ulong, BlockRoundInfo> Round2EpochBlockInfo { get; } = new(XdcConstants.InMemoryRound2Epochs, nameof(Round2EpochBlockInfo));
    protected LruCache<ValueHash256, EpochSwitchInfo> EpochSwitches { get; } = new(XdcConstants.InMemoryEpochs, nameof(EpochSwitches));

    public abstract bool IsEpochSwitchAtBlock(XdcBlockHeader header);

    public abstract bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);

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
            header = (XdcBlockHeader)Tree.FindHeader(header.ParentHash) ?? throw new InvalidOperationException($"Parent block {header.ParentHash} not found while walking to epoch switch");;
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

            masterNodes = header.ValidatorsAddress.Value.ToArray();
        }

        Snapshot snap = SnapshotManager.GetSnapshotByBlockNumber(header.Number, xdcSpec);
        if (snap is null)
        {
            return null;
        }

        Address[] penalties = ResolvePenalties(header, snap, xdcSpec);
        Address[] candidates = snap.NextEpochCandidates;

        Address[] standbyNodes = Array.Empty<Address>();

        if (masterNodes.Length != candidates.Length)
        {
            standbyNodes = candidates
                .Except(masterNodes)
                .Except(penalties)
                .ToArray();
        }

        epochSwitchInfo = new EpochSwitchInfo(masterNodes, standbyNodes, penalties, new BlockRoundInfo(header.Hash, header.ExtraConsensusData?.BlockRound ?? 0, header.Number));

        if (header.ExtraConsensusData?.QuorumCert is not null)
        {
            epochSwitchInfo.EpochSwitchParentBlockInfo = header.ExtraConsensusData.QuorumCert.ProposedBlockInfo;
        }

        EpochSwitches.Set(headerHash, epochSwitchInfo);
        return epochSwitchInfo;
    }

    protected abstract Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec);

    public EpochSwitchInfo? GetEpochSwitchInfo(Hash256 hash)
    {
        if (EpochSwitches.TryGet(hash, out EpochSwitchInfo epochSwitchInfo))
        {
            return epochSwitchInfo;
        }

        XdcBlockHeader h = (XdcBlockHeader)Tree.FindHeader(hash);
        if (h is null)
        {
            return null;
        }

        return GetEpochSwitchInfo(h);
    }

    public abstract EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert);

    public abstract BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch);
}
