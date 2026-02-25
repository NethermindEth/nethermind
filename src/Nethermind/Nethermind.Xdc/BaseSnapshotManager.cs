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

internal abstract class BaseSnapshotManager<TSnapshot> : ISnapshotManager
    where TSnapshot : Snapshot
{
    private readonly LruCache<Hash256, TSnapshot> _snapshotCache;
    private readonly BaseSnapshotDecoder<TSnapshot> _snapshotDecoder;
    private readonly IDb _snapshotDb;
    private readonly IBlockTree _blockTree;
    private readonly IMasternodeVotingContract _votingContract;
    private readonly ISpecProvider _specProvider;
    private readonly IPenaltyHandler _penaltyHandler;

    protected BaseSnapshotManager(
        IDb snapshotDb,
        IBlockTree blockTree,
        IPenaltyHandler penaltyHandler,
        IMasternodeVotingContract votingContract,
        ISpecProvider specProvider,
        BaseSnapshotDecoder<TSnapshot> snapshotDecoder,
        string cacheName
    )
    {
        _blockTree = blockTree;
        _blockTree.NewHeadBlock += OnNewHeadBlock;
        _snapshotDb = snapshotDb;
        _votingContract = votingContract;
        _specProvider = specProvider;
        _penaltyHandler = penaltyHandler;
        _snapshotDecoder = snapshotDecoder;
        _snapshotCache = new LruCache<Hash256, TSnapshot>(128, 128, cacheName);
    }

    protected IBlockTree BlockTree => _blockTree;
    protected IMasternodeVotingContract VotingContract => _votingContract;
    protected ISpecProvider SpecProvider => _specProvider;
    protected IPenaltyHandler PenaltyHandler => _penaltyHandler;

    // Explicit interface implementation to return base Snapshot type
    Snapshot? ISnapshotManager.GetSnapshotByGapNumber(long gapNumber) => GetSnapshotByGapNumber(gapNumber);

    Snapshot? ISnapshotManager.GetSnapshotByBlockNumber(long blockNumber, IXdcReleaseSpec spec) => GetSnapshotByBlockNumber(blockNumber, spec);

    void ISnapshotManager.StoreSnapshot(Snapshot snapshot)
    {
        if (snapshot is TSnapshot typedSnapshot)
        {
            StoreSnapshot(typedSnapshot);
        }
        else
        {
            throw new ArgumentException($"Snapshot must be of type {typeof(TSnapshot).Name}", nameof(snapshot));
        }
    }

    public TSnapshot? GetSnapshotByGapNumber(long gapNumber)
    {
        var gapBlockHeader = _blockTree.FindHeader(gapNumber) as XdcBlockHeader;

        if (gapBlockHeader is null)
            return null;

        TSnapshot? snapshot = _snapshotCache.Get(gapBlockHeader.Hash);
        if (snapshot is not null)
        {
            return snapshot;
        }

        Span<byte> key = gapBlockHeader.Hash.Bytes;
        if (!_snapshotDb.KeyExists(key))
            return null;
        Span<byte> value = _snapshotDb.Get(key);
        if (value.IsEmpty)
            return null;

        TSnapshot decoded = _snapshotDecoder.Decode(value);
        snapshot = decoded;
        _snapshotCache.Set(gapBlockHeader.Hash, snapshot);
        return snapshot;
    }

    public TSnapshot? GetSnapshotByBlockNumber(long blockNumber, IXdcReleaseSpec spec)
    {
        var gapBlockNum = Math.Max(0, blockNumber - blockNumber % spec.EpochLength - spec.Gap);
        return GetSnapshotByGapNumber(gapBlockNum);
    }

    public void StoreSnapshot(TSnapshot snapshot)
    {
        Span<byte> key = snapshot.HeaderHash.Bytes;

        if (_snapshotDb.KeyExists(key))
            return;

        Rlp rlpEncodedSnapshot = _snapshotDecoder.Encode(snapshot);

        _snapshotDb.Set(key, rlpEncodedSnapshot.Bytes);
        _snapshotCache.Set(snapshot.HeaderHash, snapshot);
    }

    public abstract (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(
        long blockNumber,
        Hash256 parentHash,
        IXdcReleaseSpec spec);

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
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header, round);
        if (!ISnapshotManager.IsTimeForSnapshot(header.Number, spec))
            return;

        Address[] candidates;
        if (header.IsGenesis)
            candidates = spec.GenesisMasterNodes;
        else
            candidates = _votingContract.GetCandidatesByStake(header);

        TSnapshot snapshot = CreateSnapshot(header, candidates, spec);
        StoreSnapshot(snapshot);
    }

    /// <summary>
    /// Creates a snapshot for the given header and candidates.
    /// Derived classes implement this to create their specific snapshot type.
    /// </summary>
    protected abstract TSnapshot CreateSnapshot(XdcBlockHeader header, Address[] candidates, IXdcReleaseSpec spec);
}
