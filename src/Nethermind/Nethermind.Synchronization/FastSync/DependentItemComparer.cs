// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Synchronization.FastSync
{
    internal class DependentItemComparer : IEqualityComparer<DependentItem>
    {
        private DependentItemComparer()
        {
        }

        private static DependentItemComparer? _instance;

        public static DependentItemComparer Instance
        {
            get
            {
                if (_instance is null)
                {
                    LazyInitializer.EnsureInitialized(ref _instance, () => new DependentItemComparer());
                }

                return _instance;
            }
        }

        public bool Equals(DependentItem? x, DependentItem? y)
        {
            if (x is null)
            {
                return y is null;
            }

            return y is not null && StateSyncItemComparer.Instance.Equals(x.SyncItem, y.SyncItem);

        }

        public int GetHashCode(DependentItem obj)
        {
            return StateSyncItemComparer.Instance.GetHashCode(obj.SyncItem);
        }
    }
}
