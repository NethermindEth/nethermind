// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class SnapshotManager : ISnapshotManager
{

    private LruCache<Hash256, Snapshot> _snapshotCache = new(128, 128, "XDC Snapshot cache");
    private IDb _snapshotDb { get; }
    private IPenaltyHandler _penaltyHandler { get; }

    private readonly SnapshotDecoder _snapshotDecoder = new();

    public SnapshotManager(IDb snapshotDb, IPenaltyHandler penaltyHandler)
    {
        _snapshotDb = snapshotDb;
        _penaltyHandler = penaltyHandler;
    }

    public Snapshot? GetSnapshot(Hash256 hash)
    {
        Snapshot? snapshot = _snapshotCache.Get(hash);
        if (snapshot is not null)
        {
            return snapshot;
        }

        Span<byte> key = hash.Bytes;
        if (!_snapshotDb.KeyExists(key))
            return null;
        Span<byte> value = _snapshotDb.Get(key);
        if (value.IsEmpty)
            return null;

        var decoded = _snapshotDecoder.Decode(value, RlpBehaviors.None);
        snapshot = decoded;
        _snapshotCache.Set(hash, snapshot);
        return snapshot;
    }

    public void StoreSnapshot(Snapshot snapshot)
    {
        Span<byte> key = snapshot.HeaderHash.Bytes;

        if (_snapshotDb.KeyExists(key))
            return;

        var rlpEncodedSnapshot = _snapshotDecoder.Encode(snapshot, RlpBehaviors.None);

        _snapshotDb.Set(key, rlpEncodedSnapshot.Bytes);
    }

    public (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        int maxMasternodes = spec.MaxMasternodes;
        var previousSnapshot = GetSnapshot(header.Hash);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header {header.Number}:{header.Hash.ToShortString()}");

        var candidates = previousSnapshot.NextEpochCandidates;

        if (header.Number == spec.SwitchBlock + 1)
        {
            if (candidates.Length > maxMasternodes)
            {
                Array.Resize(ref candidates, maxMasternodes);
                return (candidates, []);
            }

            return (candidates, []);
        }

        var penalties = _penaltyHandler.HandlePenalties(header.Number, header.ParentHash, candidates);

        candidates = candidates
            .Except(penalties)        // remove penalties
            .Take(maxMasternodes)     // enforce max cap
            .ToArray();

        return (candidates, penalties);
    }
}
