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

using System.Diagnostics;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed
    {
        [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
        private class DependentItem
        {
            public StateSyncItem SyncItem { get; }
            public byte[] Value { get; }
            public int Counter { get; set; }

            public bool IsAccount { get; }

            public DependentItem(StateSyncItem syncItem, byte[] value, int counter, bool isAccount = false)
            {
                SyncItem = syncItem;
                Value = value;
                Counter = counter;
                IsAccount = isAccount;
            }
        }
    }
}
