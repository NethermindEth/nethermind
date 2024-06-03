// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Verkle.Tree.History.V2;

public class LeafChangeSet : ILeafChangeSet
{
    public LeafChangeSet(IDbProvider dbProvider, ILogManager logManager)
    {
        ChangeSet = dbProvider.ForwardDiff;
    }

    private IDb ChangeSet { get; }

    public void InsertDiff(long blockNumber, LeafStoreInterface leafTable)
    {
        Span<byte> keyFull = stackalloc byte[32 + 8]; // pedersenKey + blockNumber
        BinaryPrimitives.WriteInt64BigEndian(keyFull.Slice(32), blockNumber);
        foreach (KeyValuePair<byte[], byte[]?> leafEntry in leafTable)
        {
            leafEntry.Key.CopyTo(keyFull.Slice(0, 32));
            ChangeSet.Set(keyFull, leafEntry.Value);
        }
    }

    public byte[]? GetLeaf(long blockNumber, ReadOnlySpan<byte> key)
    {
        Span<byte>
            dbKey = stackalloc byte[32 + 8]; // pedersenKey + blockNumber leafEntry.Key.CopyTo(keyFull.Slice(0, 32));
        key.CopyTo(dbKey.Slice(0, 32));
        BinaryPrimitives.WriteInt64BigEndian(dbKey.Slice(32), blockNumber);
        return ChangeSet.Get(dbKey);
    }
}
