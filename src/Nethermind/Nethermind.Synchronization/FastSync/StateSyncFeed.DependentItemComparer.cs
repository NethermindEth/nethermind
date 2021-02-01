//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed
    {
        private class DependentItemComparer : IEqualityComparer<DependentItem>
        {
            private DependentItemComparer()
            {
            }

            private static DependentItemComparer? _instance;

            public static DependentItemComparer Instance
            {
                get
                {
                    if (_instance == null)
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
}
