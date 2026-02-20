// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Linq;

namespace Nethermind.Xdc;

internal class SnapshotManager : ISnapshotManager
{

    private readonly LruCache<Hash256, Snapshot> _snapshotCache = new(128, 128, "XDC Snapshot cache");

    private readonly SnapshotDecoder _snapshotDecoder = new();
    private readonly IDb snapshotDb;
    private readonly IBlockTree blockTree;
    private readonly IMasternodeVotingContract votingContract;
    private readonly ISpecProvider specProvider;
    private readonly IPenaltyHandler penaltyHandler;

    public SnapshotManager(IDb snapshotDb, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
    {
        blockTree.NewHeadBlock += OnNewHeadBlock;
        this.snapshotDb = snapshotDb;
        this.blockTree = blockTree;
        this.votingContract = votingContract;
        this.specProvider = specProvider;
        this.penaltyHandler = penaltyHandler;
    }

    public Snapshot? GetSnapshotByGapNumber(long gapNumber)
    {
        var gapBlockHeader = blockTree.FindHeader((long)gapNumber) as XdcBlockHeader;

        if (gapBlockHeader is null)
            return null;

        Snapshot? snapshot = _snapshotCache.Get(gapBlockHeader.Hash);
        if (snapshot is not null)
        {
            return snapshot;
        }

        Span<byte> key = gapBlockHeader.Hash.Bytes;
        if (!snapshotDb.KeyExists(key))
            return null;
        Span<byte> value = snapshotDb.Get(key);
        if (value.IsEmpty)
            return null;

        Snapshot decoded = _snapshotDecoder.Decode(value);
        snapshot = decoded;
        _snapshotCache.Set(gapBlockHeader.Hash, snapshot);
        return snapshot;
    }

    public Snapshot? GetSnapshotByBlockNumber(long blockNumber, IXdcReleaseSpec spec)
    {
        var gapBlockNum = Math.Max(0, blockNumber - blockNumber % spec.EpochLength - spec.Gap);
        return GetSnapshotByGapNumber(gapBlockNum);
    }

    public void StoreSnapshot(Snapshot snapshot)
    {
        Span<byte> key = snapshot.HeaderHash.Bytes;

        if (snapshotDb.KeyExists(key))
            return;

        Rlp rlpEncodedSnapshot = _snapshotDecoder.Encode(snapshot);

        snapshotDb.Set(key, rlpEncodedSnapshot.Bytes);
        _snapshotCache.Set(snapshot.HeaderHash, snapshot);
    }

    public (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec)
    {
        int maxMasternodes = spec.MaxMasternodes;
        Snapshot previousSnapshot = GetSnapshotByBlockNumber(blockNumber, spec);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header #{blockNumber}");

        Address[] candidates = previousSnapshot.NextEpochCandidates;

        if (blockNumber == spec.SwitchBlock + 1)
        {
            if (candidates.Length > maxMasternodes)
            {
                Array.Resize(ref candidates, maxMasternodes);
                return (candidates, []);
            }

            return (candidates, []);
        }

        Address[] penalties = penaltyHandler.HandlePenalties(blockNumber, parentHash, candidates);

        candidates = candidates
            .Except(penalties)        // remove penalties
            .Take(maxMasternodes)     // enforce max cap
            .ToArray();

        return (candidates, penalties);
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        UpdateMasterNodes((XdcBlockHeader)e.Block.Header);
    }

    private void UpdateMasterNodes(XdcBlockHeader header)
    {
        ulong round;
        if (header.IsGenesis)
            round = 0;
        else
            round = header.ExtraConsensusData.BlockRound;
        // Could consider dropping the round parameter here, since the consensus parameters used here should not change 
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(header, round);
        if (!ISnapshotManager.IsTimeForSnapshot(header.Number, spec))
            return;

        Address[] candidates;
        if (header.IsGenesis)
            candidates = spec.GenesisMasterNodes;
        else
            candidates = votingContract.GetCandidatesByStake(header);

        Snapshot snapshot = new(header.Number, header.Hash, candidates);
        StoreSnapshot(snapshot);
    }
}
