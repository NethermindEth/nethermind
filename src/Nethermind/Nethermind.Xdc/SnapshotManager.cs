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

namespace Nethermind.Xdc;
internal class SnapshotManager : ISnapshotManager
{

    private LruCache<Hash256, Snapshot> _snapshotsByHash = new(128, 128, "XDC Snapshot cache");
    private IBlockTree _tree { get; }
    private IDb _snapshotDb { get; }

    private SnapshotDecoder _snapshotDecoder = new();


    public SnapshotManager(IDb snapshotDb, IBlockTree tree)
    {
        _snapshotDb = snapshotDb;
        _tree = tree;
    }

    public Snapshot? GetSnapshot(Hash256 hash)
    {
        Snapshot snapshot = _snapshotsByHash.Get(hash);
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
        SnapshotDecoder snapshotDecoder = new();

        var decoded = snapshotDecoder.Decode(value, RlpBehaviors.None);
        snapshot = decoded;
        _snapshotsByHash.Set(hash, snapshot);
        return snapshot;
    }

    public Snapshot? GetSnapshotByHeader(XdcBlockHeader? header)
    {
        if (header is null)
            return null;
        return GetSnapshot(header.Hash);
    }

    public Snapshot? GetSnapshotByHeaderNumber(ulong number, ulong xdcEpoch, ulong xdcGap)
    {
        ulong gapBlockNum = Math.Max(0, number - number % xdcEpoch - xdcGap);

        return GetSnapshotByGapNumber(gapBlockNum);
    }


    public Snapshot? GetSnapshotByGapNumber(ulong gapBlockNum)
    {
        Hash256 gapBlockHash = _tree.FindHeader((long)gapBlockNum)?.Hash;

        if (gapBlockHash is null)
            return null;

        return GetSnapshot(gapBlockHash);
    }

    public bool StoreSnapshot(Snapshot snapshot)
    {
        if (snapshot is null)
            return false;
        Span<byte> key = snapshot.HeaderHash.Bytes;

        if (_snapshotDb.KeyExists(key))
            return false;

        var rlpEncodedSnapshot = _snapshotDecoder.Encode(snapshot, RlpBehaviors.None);

        _snapshotDb.Set(key, rlpEncodedSnapshot.Bytes);
        return true;
    }

    public Address[] CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader)
    {
        Snapshot snapshot = GetSnapshotByHeader(xdcHeader);
        if (snapshot is null)
            throw new InvalidOperationException($"No snapshot found for header {xdcHeader.Number}:{xdcHeader.Hash.ToShortString()}");

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

    public Address[] GetMasternodes(XdcBlockHeader xdcHeader)
    {
        Snapshot snapshot = GetSnapshotByHeader(xdcHeader);
        if (snapshot is null)
            throw new InvalidOperationException($"No snapshot found for header {xdcHeader.Number}:{xdcHeader.Hash.ToShortString()}");
        return snapshot.MasterNodes;
    }

    public Address[] GetPenalties(XdcBlockHeader xdcHeader)
    {
        Snapshot snapshot = GetSnapshotByHeader(xdcHeader);
        if (snapshot is null)
            throw new InvalidOperationException($"No snapshot found for header {xdcHeader.Number}:{xdcHeader.Hash.ToShortString()}");
        return snapshot.PenalizedNodes;
    }
}
