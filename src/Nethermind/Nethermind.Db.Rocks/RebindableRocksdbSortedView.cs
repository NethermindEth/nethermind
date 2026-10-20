// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

internal sealed class RebindableRocksdbSortedView(Iterator iterator, ReadOptions readOptions, DbOnTheRocks database,
    int lowerCapacity, int upperCapacity, bool totalOrder) : RocksdbSortedView(iterator, readOptions), IRebindableSortedView
{
    public bool TryResetBounds(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
    {
        if (firstKeyInclusive.Length > lowerCapacity || lastKeyExclusive.Length > upperCapacity
            || (!totalOrder && database.CrossesPrefixBucket(firstKeyInclusive, lastKeyExclusive))) return false;

        ReadOptions.UpdateIterateBounds(firstKeyInclusive, lastKeyExclusive);
        Restart();
        return true;
    }
}
