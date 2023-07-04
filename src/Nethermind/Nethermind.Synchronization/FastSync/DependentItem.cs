// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
    internal class DependentItem
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
