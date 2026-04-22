// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

    protected BaseSnapshotManager(
        IDb snapshotDb,
        IBlockTree blockTree,
        IMasternodeVotingContract votingContract,
        ISpecProvider specProvider,
        BaseSnapshotDecoder<TSnapshot> snapshotDecoder,
        string cacheName
    )
    {
        _blockTree = blockTree;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        _snapshotDb = snapshotDb;
        _votingContract = votingContract;
        _specProvider = specProvider;
        _snapshotDecoder = snapshotDecoder;
        _snapshotCache = new LruCache<Hash256, TSnapshot>(128, 128, cacheName);
    }

    protected IMasternodeVotingContract VotingContract => _votingContract;

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
        if (_blockTree.FindHeader(gapNumber) is not XdcBlockHeader gapBlockHeader)
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
        long gapBlockNum = Math.Max(0, blockNumber - blockNumber % spec.EpochLength - spec.Gap);
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

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Hash is null || !_blockTree.WasProcessed(e.Block.Number, e.Block.Hash))
            return;
        UpdateMasterNodes((XdcBlockHeader)e.Block.Header);
    }

    private void UpdateMasterNodes(XdcBlockHeader header)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header);
        if (!ISnapshotManager.IsTimeForSnapshot(header.Number, spec))
            return;

        TSnapshot snapshot = CreateSnapshot(header, spec);
        StoreSnapshot(snapshot);
    }

    protected abstract TSnapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec);

    public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
}
