// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Nethermind.Crypto;

namespace Nethermind.Xdc;
internal class SnapshotManager : ISnapshotManager
{

    private LruCache<Hash256, Snapshot> _snapshotCache = new(128, 128, "XDC Snapshot cache");
    private IDb _snapshotDb { get; }

    private readonly SnapshotDecoder _snapshotDecoder = new();

    public SnapshotManager(IDb snapshotDb)
    {
        _snapshotDb = snapshotDb;
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

    public Address[] CalculateNextEpochMasternodes(Snapshot? snapshot)
    {
        // TODO : will possibly need to truncate or pad the masternode list to a fixed size
        Address[] masternodes = new Address[snapshot.MasterNodes.Length - snapshot.PenalizedNodes.Length];

        int index = 0;
        foreach (var addr in snapshot.MasterNodes)
        {
            if (snapshot.PenalizedNodes.Contains(addr))
            {
                continue;
            }

            masternodes[index++] = addr;
        }
        return masternodes;
    }
}
