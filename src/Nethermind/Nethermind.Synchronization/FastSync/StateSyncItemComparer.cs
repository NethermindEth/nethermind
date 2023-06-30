// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Synchronization.FastSync;

public class StateSyncItemComparer: IEqualityComparer<StateSyncItem>
{
    private StateSyncItemComparer()
    {
    }

    private static StateSyncItemComparer? _instance;

    public static StateSyncItemComparer Instance
    {
        get
        {
            if (_instance is null)
            {
                LazyInitializer.EnsureInitialized(ref _instance, () => new StateSyncItemComparer());
            }

            return _instance;
        }
    }

    public bool Equals(StateSyncItem? x, StateSyncItem? y)
    {
        if (x is null)
        {
            return y is null;
        }

        if (y is null) return false;


        bool checkForHash = x.Hash == y.Hash;
        bool checkAccountPath = x.PathNibbles.SequenceEqual(y.PathNibbles);
        bool checkPathNibbles = x.AccountPathNibbles.SequenceEqual(y.AccountPathNibbles);
        return checkForHash && checkAccountPath && checkPathNibbles;
    }

    public int GetHashCode(StateSyncItem obj)
    {
        return HashCode.Combine(obj.Hash, obj.PathNibbles, obj.AccountPathNibbles);
    }
}
