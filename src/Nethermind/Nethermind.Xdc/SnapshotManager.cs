// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Linq;

namespace Nethermind.Xdc;
internal class SnapshotManager(IDb snapshotDb, IBlockTree blockTree, IPenaltyHandler penaltyHandler) : ISnapshotManager
{

    private LruCache<Hash256, Snapshot> _snapshotCache = new(128, 128, "XDC Snapshot cache");

    private readonly SnapshotDecoder _snapshotDecoder = new();

    public Snapshot? GetSnapshotByGapNumber(ulong gapNumber)
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

        var decoded = _snapshotDecoder.Decode(value);
        snapshot = decoded;
        _snapshotCache.Set(gapBlockHeader.Hash, snapshot);
        return snapshot;
    }

    public Snapshot? GetSnapshotByBlockNumber(long blockNumber, IXdcReleaseSpec spec)
    {
        var gapBlockNum = Math.Max(0, blockNumber - blockNumber % spec.EpochLength - spec.Gap);
        return GetSnapshotByGapNumber((ulong)gapBlockNum);
    }

    public void StoreSnapshot(Snapshot snapshot)
    {
        Span<byte> key = snapshot.HeaderHash.Bytes;

        if (snapshotDb.KeyExists(key))
            return;

        var rlpEncodedSnapshot = _snapshotDecoder.Encode(snapshot);

        snapshotDb.Set(key, rlpEncodedSnapshot.Bytes);
        _snapshotCache.Set(snapshot.HeaderHash, snapshot);
    }

    public (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec)
    {
        int maxMasternodes = spec.MaxMasternodes;
        var previousSnapshot = GetSnapshotByBlockNumber(blockNumber, spec);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header #{blockNumber}");

        var candidates = previousSnapshot.NextEpochCandidates;

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
}
