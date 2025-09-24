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
    private IXdcConfig _xdcConfig { get; }
    private IDb _snapshotDb { get; }

    private SnapshotDecoder _snapshotDecoder = new();


    public SnapshotManager(IDb snapshotDb, IBlockTree tree, IXdcConfig xdcConfig)
    {
        _snapshotDb = snapshotDb;
        _tree = tree;
        _xdcConfig = xdcConfig;
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

        var stream = new RlpStream(value.ToArray());

        var decoded = snapshotDecoder.Decode(stream, RlpBehaviors.None);
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

    public Snapshot? GetSnapshotByHeaderNumber(ulong number)
    {
        ulong gapBlockNum = Math.Max(0, number - number % _xdcConfig.Epoch - _xdcConfig.Gap);

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
        Span<byte> key = snapshot.Hash.Bytes;

        if (_snapshotDb.KeyExists(key))
            return false;

        var contentLength = _snapshotDecoder.GetLength(snapshot, RlpBehaviors.None);
        RlpStream stream = new(contentLength);
        _snapshotDecoder.Encode(stream, snapshot, RlpBehaviors.None);

        stream.Reset();
        Span<byte> value = stream.Read(stream.Length);

        _snapshotDb.PutSpan(key, value);
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
