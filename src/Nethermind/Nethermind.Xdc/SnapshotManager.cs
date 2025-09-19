// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
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

    private ConcurrentDictionary<Hash256, Snapshot> _snapshotsByHash = new();
    private IBlockTree _tree { get; }
    private IXdcConfig _xdcConfig { get; }
    private IDb _snapshotDb { get; }

    public SnapshotManager(IDb snapshotDb, IBlockTree tree, IXdcConfig xdcConfig)
    {
        _snapshotDb = snapshotDb;
        _tree = tree;
        _xdcConfig = xdcConfig;
    }


    public void TryCacheSnapshot(Snapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _snapshotsByHash.TryAdd(snapshot.Hash, snapshot);
    }

    public bool TryGetSnapshot(Hash256 hash, out Snapshot snapshot)
    {
        if (_snapshotsByHash.TryGetValue(hash, out snapshot))
        {
            return true;
        }
        Span<byte> key = hash.Bytes;
        if (!_snapshotDb.KeyExists(key))
        {
            snapshot = null;
            return false;
        }
        Span<byte> value = _snapshotDb.Get(key);
        if (value.IsEmpty)
        {
            snapshot = null;
            return false;
        }
        SnapshotDecoder snapshotDecoder = new();

        var stream = new RlpStream(value.ToArray());

        var decoded = snapshotDecoder.Decode(stream, RlpBehaviors.None);
        snapshot = decoded;
        _snapshotsByHash.TryAdd(hash, snapshot);
        return true;
    }

    public bool TryGetSnapshot(XdcBlockHeader header, out Snapshot snapshot)
    {
        if (header is null)
        {
            snapshot = null;
            return false;
        }
        return TryGetSnapshot(header.Hash, out snapshot);
    }

    public bool TryGetSnapshot(ulong number, bool isGapNumber, out Snapshot snap)
    {
        ulong gapBlockNum;
        if (isGapNumber)
        {
            gapBlockNum = number;
        } else
        {
            gapBlockNum = Math.Max(0, number - number % _xdcConfig.Epoch - _xdcConfig.Gap);
        }

        Hash256 gapBlockHash = _tree.FindHeader((long)gapBlockNum)?.Hash;

        if (gapBlockHash is null)
        {
            snap = null;
            return false;
        }

        return TryGetSnapshot(gapBlockHash, out snap);
    }

    public bool TryStoreSnapshot(Snapshot snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }
        Span<byte> key = snapshot.Hash.Bytes;


        if (_snapshotDb.KeyExists(key))
        {
            return false;
        }

        SnapshotDecoder snapshotDecoder = new();

        var contentLength = snapshotDecoder.GetLength(snapshot, RlpBehaviors.None);
        RlpStream stream = new(contentLength);
        snapshotDecoder.Encode(stream, snapshot, RlpBehaviors.None);

        stream.Reset();
        Span<byte> value = stream.Read(stream.Length);

        _snapshotDb.Set(key, value.ToArray());
        return true;
    }
}
