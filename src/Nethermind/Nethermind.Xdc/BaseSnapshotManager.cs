// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Logging;
using Nethermind.State;

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
    private readonly IStateReader _stateReader;
    private readonly ILogger _logger;

    protected BaseSnapshotManager(
        IDb snapshotDb,
        IBlockTree blockTree,
        IMasternodeVotingContract votingContract,
        ISpecProvider specProvider,
        IStateReader stateReader,
        ILogManager logManager,
        BaseSnapshotDecoder<TSnapshot> snapshotDecoder,
        string cacheName
    )
    {
        _blockTree = blockTree;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        _snapshotDb = snapshotDb;
        _votingContract = votingContract;
        _specProvider = specProvider;
        _stateReader = stateReader;
        _logger = logManager.GetClassLogger<BaseSnapshotManager<TSnapshot>>();
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
        TSnapshot? snapshot = GetSnapshot(gapBlockHeader.Hash);
        snapshot ??= TryRecoverSnapshot(gapBlockHeader);

        return snapshot;
    }

    protected TSnapshot? GetSnapshot(Hash256 headerHash)
    {
        TSnapshot? snapshot = _snapshotCache.Get(headerHash);
        if (snapshot is not null)
        {
            return snapshot;
        }

        Span<byte> key = headerHash.Bytes;
        if (!_snapshotDb.KeyExists(key))
            return null;
        Span<byte> value = _snapshotDb.Get(key);
        if (value.IsEmpty)
            return null;

        TSnapshot decoded = _snapshotDecoder.Decode(value);
        snapshot = decoded;
        _snapshotCache.Set(headerHash, snapshot);
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

    private TSnapshot? TryRecoverSnapshot(XdcBlockHeader gapBlockHeader)
    {
        if (!ISnapshotManager.IsTimeForSnapshot(gapBlockHeader.Number, _specProvider.GetXdcSpec(gapBlockHeader)))
            return null;

        if (gapBlockHeader.Hash is null || !_blockTree.WasProcessed(gapBlockHeader.Number, gapBlockHeader.Hash))
        {
            if (_logger.IsDebug) _logger.Debug($"Cannot recover snapshot for block {gapBlockHeader.Number} ({gapBlockHeader.Hash}): block not processed");
            return null;
        }

        if (!_stateReader.HasStateForBlock(gapBlockHeader))
        {
            if (_logger.IsDebug) _logger.Debug($"Cannot recover snapshot for block {gapBlockHeader.Number} ({gapBlockHeader.Hash}): state unavailable");
            return null;
        }

        try
        {
            UpdateMasterNodes(gapBlockHeader);
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Snapshot recovery failed for block {gapBlockHeader.Number} ({gapBlockHeader.Hash}): {ex.Message}");
            return null;
        }

        TSnapshot? snapshot = GetSnapshot(gapBlockHeader.Hash!);

        if (snapshot is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Snapshot recovery produced no snapshot for block {gapBlockHeader.Number} ({gapBlockHeader.Hash})");
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Recovered snapshot for block {gapBlockHeader.Number} ({gapBlockHeader.Hash})");
        }

        return snapshot;
    }

    protected abstract TSnapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec);

    public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
}
