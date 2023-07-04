// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

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
            return x?.SyncItem.Hash == y?.SyncItem.Hash;
        }

        public int GetHashCode(DependentItem obj)
        {
            return obj?.SyncItem.Hash.GetHashCode() ?? 0;
        }
    }
}
