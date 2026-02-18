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

internal abstract class BaseEpochSwitchManager : IEpochSwitchManager
{
    protected BaseEpochSwitchManager(ISpecProvider xdcSpecProvider, IBlockTree tree, ISnapshotManager snapshotManager)
    {
        _xdcSpecProvider = xdcSpecProvider;
        _tree = tree;
        _snapshotManager = snapshotManager;
    }

    protected ISpecProvider _xdcSpecProvider { get; }
    protected IBlockTree _tree { get; }
    protected ISnapshotManager _snapshotManager { get; }
    protected LruCache<ulong, BlockRoundInfo> _round2EpochBlockInfo { get; set; } = new(XdcConstants.InMemoryRound2Epochs, nameof(_round2EpochBlockInfo));
    protected LruCache<ValueHash256, EpochSwitchInfo> _epochSwitches { get; set; } = new(XdcConstants.InMemoryEpochs, nameof(_epochSwitches));

    public abstract bool IsEpochSwitchAtBlock(XdcBlockHeader header);

    public abstract bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);

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

        var snap = _snapshotManager.GetSnapshotByBlockNumber(header.Number, xdcSpec);
        if (snap is null)
        {
            return null;
        }

        Address[] penalties = ResolvePenalties(header, snap, xdcSpec);
        Address[] candidates = snap.NextEpochCandidates;

        var standbyNodes = Array.Empty<Address>();

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

        _epochSwitches.Set(header.Hash, epochSwitchInfo);
        return epochSwitchInfo;
    }

    protected abstract Address[] ResolvePenalties(XdcBlockHeader header, Snapshot snapshot, IXdcReleaseSpec spec);

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

    public abstract EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCert);

    public abstract BlockRoundInfo? GetBlockByEpochNumber(ulong targetEpoch);
}
